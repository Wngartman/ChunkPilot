using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using System.Windows.Threading;
using ChunkPilot.App.CreateServerLive;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Web.WebView2.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using ImageSharpResizeMode = SixLabors.ImageSharp.Processing.ResizeMode;
using ImageSharpSize = SixLabors.ImageSharp.Size;

namespace ChunkPilot.App.WebUi;

public partial class WebUiWindow : Window
{
    internal static readonly TimeSpan ActivePresentationRefreshInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan QuiescentPresentationRefreshInterval = TimeSpan.FromSeconds(3);
    internal static bool RequiresFullPresentationRefresh(string method) =>
        method != "workspace.load" && !method.StartsWith("connectivity.", StringComparison.Ordinal);
    internal static bool IsDeferredLifecycleMethod(string method) =>
        method is "servers.start" or "servers.stop" or "servers.restart";
    internal static bool IsDeferredOperationMethod(string method) =>
        IsDeferredLifecycleMethod(method) || method is "servers.delete" or "servers.createManagedCopy" or "versions.install";
    internal static bool ShouldRetryRendererFailure(
        bool isClosed,
        bool retryUsed,
        CoreWebView2ProcessFailedKind kind) =>
        !isClosed && !retryUsed &&
        kind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.FrameRenderProcessExited;
    internal static CoreWebView2EnvironmentOptions CreateEnvironmentOptions(string additionalBrowserArguments = "") => new()
    {
        AdditionalBrowserArguments = additionalBrowserArguments,
        // ChunkPilot owns renderer-failure recovery and diagnostics. Disabling WebView2's
        // separate crash uploader also prevents its crashpad helper from outliving the
        // native window and retaining the app-specific profile after an immediate close.
        IsCustomCrashReportingEnabled = true
    };
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private readonly MainViewModel viewModel;
    private readonly AgentClient client;
    private readonly DispatcherTimer refreshTimer;
    private readonly WebUiSnapshotMapper snapshots = new();
    private readonly AgentVanillaCreationGateway creation;
    private readonly AgentPaperCreationGateway paperCreation;
    private readonly AgentManagedLoaderCreationGateway loaderCreation;
    private WebUiBridgeHost? bridge;
    private VanillaVersionCatalog? creationCatalog;
    private PaperVersionCatalog? paperVersionCatalog;
    private readonly Dictionary<string, PaperBuildCatalog> paperBuildCatalogs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ManagedLoaderPlatform, ManagedLoaderVersionCatalog> loaderVersionCatalogs = [];
    private readonly Dictionary<string, ManagedLoaderBuildCatalog> loaderBuildCatalogs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, LifecycleWebUiOperation> lifecycleOperations = [];
    private readonly Dictionary<Guid, CreationWebUiOperation> creationOperations = [];
    private readonly HashSet<Guid> observedContentOperations = [];
    private readonly HashSet<Guid> observedUpdateOperations = [];
    private readonly Dictionary<string, CatalogItem> modpackCatalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> modpackImageCache = new(StringComparer.Ordinal);
    private Guid sessionId;
    private string sessionCapability = "";
    private bool refreshInProgress;
    private bool initializing;
    private bool rendererRetryUsed;
    private bool closed;
    private DateTimeOffset lastHeartbeatAt;
    private DateTimeOffset lastPresentationRefreshAt;
    private readonly WebUiLocalPluginTokenStore localPluginTokens = new();
    private readonly WebUiServerImportTokenStore localImportTokens = new();
    private readonly WebUiLegacyArtifactTokenStore legacyArtifactTokens = new();
    private readonly HttpClient modpackImages = new() { Timeout = TimeSpan.FromSeconds(20) };

    public WebUiWindow(MainViewModel viewModel, AgentClient client)
    {
        InitializeComponent();
        Browser.DefaultBackgroundColor = WebUiNativeTheme.ResolveWebViewColor("AppSurfaceCanvas");
        this.viewModel = viewModel;
        this.client = client;
        creation = new AgentVanillaCreationGateway(client);
        paperCreation = new AgentPaperCreationGateway(client);
        loaderCreation = new AgentManagedLoaderCreationGateway(client);
        modpackImages.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ChunkPilot/1.3.0 (local Windows Minecraft server manager)");
        DataContext = viewModel;
        refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, RefreshTimerOnTick, Dispatcher);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public void ConfigureSession(Guid id, string capability)
    {
        sessionId = id;
        sessionCapability = capability;
        lastHeartbeatAt = DateTimeOffset.MinValue;
    }

    public void TransitionToShell() => refreshTimer.Start();

    public void ShowStartupFailure()
    {
        FailureDetail.Text = viewModel.Startup.FailureDetail ?? "Could not connect to the ChunkPilot service.";
        FailureSurface.Visibility = Visibility.Visible;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await InitializeWebViewAsync().ConfigureAwait(true);

    private async Task InitializeWebViewAsync()
    {
        if (initializing)
            return;
        initializing = true;
        FailureSurface.Visibility = Visibility.Collapsed;
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var dataRoot = Environment.GetEnvironmentVariable("CHUNKPILOT_DATA_ROOT");
            var root = string.IsNullOrWhiteSpace(dataRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChunkPilot")
                : Path.GetFullPath(dataRoot);
            var profile = Path.Combine(root, "WebView2", "CurrentProfile");
            Directory.CreateDirectory(profile);
            var environmentOptions = CreateEnvironmentOptions();
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile, options: environmentOptions).ConfigureAwait(true);
            await Browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            ConfigureWebView(Browser.CoreWebView2);
            bridge?.Dispose();
            bridge = new WebUiBridgeHost(Browser.CoreWebView2, viewModel, snapshots, DispatchCancellableAsync);
            var assetRoot = Path.Combine(AppContext.BaseDirectory, "WebUi");
            if (!File.Exists(Path.Combine(assetRoot, "index.html")))
                throw new FileNotFoundException("The locally bundled WebUI assets are missing from this build.", Path.Combine(assetRoot, "index.html"));
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "chunkpilot.local", assetRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            Browser.Source = new Uri(WebUiProtocol.EntryPoint, UriKind.Absolute);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowFailure("Microsoft Edge WebView2 Runtime is required. ChunkPilot did not download or open anything automatically. Repair or install the Evergreen WebView2 Runtime, then retry.");
        }
        catch (Exception exception)
        {
            ShowFailure(SecretRedactor.Redact(exception.Message));
        }
        finally
        {
            initializing = false;
        }
    }

    private void ConfigureWebView(CoreWebView2 core)
    {
        core.Settings.IsScriptEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.NavigationStarting += CoreOnNavigationStarting;
        core.NewWindowRequested += CoreOnNewWindowRequested;
        core.DownloadStarting += CoreOnDownloadStarting;
        core.ProcessFailed += CoreOnProcessFailed;
        Browser.PreviewKeyDown += BrowserOnPreviewKeyDown;
        Browser.AllowDrop = false;
    }

    private void CoreOnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (WebUiProtocol.IsTrustedSource(e.Uri))
            return;
        e.Cancel = true;
        OpenExternalHttps(e.Uri);
    }

    private void CoreOnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternalHttps(e.Uri);
    }

    private static void CoreOnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
    }

    private void CoreOnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (closed)
            return;
        if (ShouldRetryRendererFailure(closed, rendererRetryUsed, e.ProcessFailedKind))
        {
            rendererRetryUsed = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (closed)
                    return;
                try
                {
                    Browser.Reload();
                }
                catch (Exception exception) when (exception is InvalidOperationException or COMException)
                {
                    ShowFailure("The interface renderer could not be recovered. Managed servers remain owned by the ChunkPilot Agent. Exit ChunkPilot safely, then reopen it.");
                }
            }, DispatcherPriority.Background);
            return;
        }
        ShowFailure("The interface renderer stopped unexpectedly. Managed servers remain owned by the ChunkPilot Agent. Retry the interface or exit ChunkPilot safely.");
    }

    private static void OpenExternalHttps(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static void BrowserOnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key is Key.Add or Key.Subtract or Key.OemPlus or Key.OemMinus or Key.D0)
            e.Handled = true;
        if (e.Key is Key.F5 or Key.F12)
            e.Handled = true;
    }

    private Task<JsonNode?> DispatchCancellableAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken) => method switch
        {
            "modpacks.versions" => LoadModpackVersionsAsync(parameters, cancellationToken),
            "modpacks.cache" => SearchModpacksAsync(parameters, cacheOnly: true, cancellationToken),
            "modpacks.search" => SearchModpacksAsync(parameters, cacheOnly: false, cancellationToken),
            "modpacks.resolveLink" => ResolveModpackLinkAsync(parameters, cancellationToken),
            _ => DispatchAsync(method, parameters)
        };

    private async Task<JsonNode?> DispatchAsync(string method, JsonObject parameters)
    {
        switch (method)
        {
            case "snapshot.refresh":
                await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
                return snapshots.Capture(viewModel);
            case "snapshot.selectServer":
                if (TryServer(parameters, out var selected))
                    viewModel.SelectServerCommand.Execute(selected);
                else
                    viewModel.NavigateCommand.Execute("Servers");
                return snapshots.Capture(viewModel);
            case "window.drag":
                DragFromWebUi();
                return Accepted(method);
            case "window.minimize":
                WindowState = WindowState.Minimized;
                return Accepted(method);
            case "window.toggleMaximize":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return Accepted(method);
            case "window.close":
                Close();
                return Accepted(method);
            case "servers.start":
                return BeginLifecycleOperation(method, RequireServer(parameters));
            case "servers.stop":
                return BeginLifecycleOperation(method, RequireServer(parameters));
            case "servers.restart":
                return BeginLifecycleOperation(method, RequireServer(parameters));
            case "servers.openFolder":
            case "files.openFolder":
                Select(parameters);
                viewModel.OpenServerFolderCommand.Execute(null);
                break;
            case "diagnostics.openLogs":
                Select(parameters);
                viewModel.OpenLogsFolderCommand.Execute(null);
                break;
            case "diagnostics.bundle":
                Select(parameters);
                await viewModel.CreateDiagnosticBundleCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "servers.import":
                await viewModel.AddServerCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "servers.rename":
                Select(parameters);
                await viewModel.RenameServerCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "servers.changeIcon":
                Select(parameters);
                await viewModel.InstallServerIconCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "servers.deletePreflight":
            {
                var server = RequireServer(parameters).Definition;
                return JsonSerializer.SerializeToNode(
                    await client.SendAsync<ServerDeletionPreflight>("ServerDeletionPreflight",
                        new ServerDeletionPreflightRequest(server.Id)).ConfigureAwait(true),
                    WebUiProtocol.Json);
            }
            case "servers.delete":
            {
                var server = RequireServer(parameters);
                var tokenText = RequiredString(parameters, "preflightToken", 64);
                if (!Guid.TryParse(tokenText, out var token))
                    throw new ArgumentException("Deletion preflight token is invalid.");
                var modeText = RequiredString(parameters, "mode", 32);
                if (!Enum.TryParse<ServerDeletionMode>(modeText, true, out var mode) || !Enum.IsDefined(mode))
                    throw new ArgumentException("Deletion mode is invalid.");
                return BeginDeletionOperation(server, new ServerDeletionRequest(
                    server.Definition.Id,
                    token,
                    mode,
                    parameters["confirmationName"]?.GetValue<string>() ?? "",
                    parameters["acknowledgeWorldDeletion"]?.GetValue<bool?>() ?? false,
                    parameters["acknowledgeManagedBackupDeletion"]?.GetValue<bool?>() ?? false));
            }
            case "servers.createManagedCopy":
            {
                var server = RequireServer(parameters);
                var tokenText = RequiredString(parameters, "preflightToken", 64);
                if (!Guid.TryParse(tokenText, out var token))
                    throw new ArgumentException("Ownership review token is invalid.");
                return BeginManagedCopyOperation(server,
                    new ManagedCopyConversionRequest(server.Definition.Id, token));
            }
            case "plugins.openFolder":
            case "mods.openFolder":
            {
                var server = RequireServer(parameters).Definition;
                var folderName = server.Ecosystem is ServerEcosystem.Fabric or ServerEcosystem.Quilt or
                    ServerEcosystem.Forge or ServerEcosystem.NeoForge
                    ? "mods"
                    : "plugins";
                var folder = Path.Combine(server.RootPath, folderName);
                Directory.CreateDirectory(folder);
                var result = new WindowsFolderLauncher().OpenExisting(folder);
                if (!result.Success)
                    throw new InvalidOperationException(result.Message);
                return Accepted(method);
            }
            case "appearance.chooseIcon":
                return await ChooseAppearanceIconAsync().ConfigureAwait(true);
            case "modpacks.providers":
                return JsonSerializer.SerializeToNode(
                    (await client.SendAsync<IReadOnlyList<CatalogProviderStatus>>(
                        "CatalogProviderStatuses").ConfigureAwait(true))
                    .Where(status => status.Provider is CatalogProvider.Modrinth or CatalogProvider.CurseForge),
                    WebUiProtocol.Json);
            case "modpacks.versions":
                return await LoadModpackVersionsAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            case "modpacks.cache":
                return await SearchModpacksAsync(parameters, cacheOnly: true, CancellationToken.None).ConfigureAwait(true);
            case "modpacks.search":
                return await SearchModpacksAsync(parameters, cacheOnly: false, CancellationToken.None).ConfigureAwait(true);
            case "modpacks.resolveLink":
                return await ResolveModpackLinkAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            case "modpacks.image":
                return await LoadModpackImageAsync(parameters).ConfigureAwait(true);
            case "modpacks.chooseLocal":
                return await ChooseLocalServerImportAsync(parameters).ConfigureAwait(true);
            case "creation.chooseLegacyArtifact":
                return await ChooseLegacyServerArtifactAsync(parameters).ConfigureAwait(true);
            case "plugins.chooseLocal":
            case "mods.chooseLocal":
                return await ChooseLocalAddonAsync(parameters).ConfigureAwait(true);
            case "plugins.installLocal":
            case "mods.installLocal":
            {
                Select(parameters);
                var token = RequiredString(parameters, "token", 128);
                var source = ConsumeLocalPlugin(token, RequireServer(parameters).Definition.Id);
                var result = await client.SendAsync<OperationResult>("InstallJar",
                    new JarInstallRequest(viewModel.SelectedServer!.Definition.Id, source,
                        parameters["restartIfRunning"]?.GetValue<bool?>() ?? false)).ConfigureAwait(true);
                await viewModel.LoadWebUiInventoryAsync().ConfigureAwait(true);
                await bridge!.PublishSnapshotAsync().ConfigureAwait(true);
                return JsonSerializer.SerializeToNode(result, WebUiProtocol.Json);
            }
            case "plugins.providers":
            case "mods.providers":
                Select(parameters);
                return JsonSerializer.SerializeToNode(
                    await client.SendAsync<IReadOnlyList<PluginProviderStatus>>("PluginProviders",
                        new ServerIdRequest(RequireServer(parameters).Definition.Id)).ConfigureAwait(true),
                    WebUiProtocol.Json);
            case "plugins.search":
            case "mods.search":
            {
                Select(parameters);
                var results = await client.SendAsync<IReadOnlyList<PluginProject>>("PluginSearch",
                    new PluginSearchRequest(RequireServer(parameters).Definition.Id,
                        parameters["search"]?.GetValue<string>()?.Trim() ?? "",
                        RequiredInt(parameters, "limit", 1, 40, 20))).ConfigureAwait(true);
                // Provider image URLs are intentionally not sent to the renderer. Production CSP
                // permits only app-local images; a future native image cache can add them safely.
                return JsonSerializer.SerializeToNode(results.Select(project => new
                {
                    provider = project.Provider.ToString(),
                    projectId = project.ProjectId,
                    slug = project.Slug,
                    name = project.Name,
                    author = project.Author,
                    summary = project.Summary,
                    downloads = project.Downloads,
                    updatedAt = project.UpdatedAt,
                    serverSide = project.ServerSide,
                    clientSide = project.ClientSide,
                    clientRequirement = project.ClientRequirement,
                    kind = project.Kind.ToString()
                }).ToArray(), WebUiProtocol.Json);
            }
            case "plugins.release":
            case "mods.release":
            {
                Select(parameters);
                var release = await client.SendAsync<PluginRelease?>("PluginRelease",
                    new PluginReleaseRequest(RequireServer(parameters).Definition.Id,
                        RequiredString(parameters, "projectId", 80))).ConfigureAwait(true);
                return JsonSerializer.SerializeToNode(release is null ? null : new
                {
                    provider = release.Provider.ToString(),
                    projectId = release.ProjectId,
                    versionId = release.VersionId,
                    versionName = release.VersionName,
                    minecraftVersion = release.MinecraftVersion,
                    loader = release.Loader,
                    releaseChannel = release.ReleaseChannel,
                    publishedAt = release.PublishedAt,
                    fileName = release.FileName,
                    sizeBytes = release.SizeBytes,
                    integrity = release.Sha512.Length == 128 ? "sha512" : "unavailable",
                    serverSide = release.ServerSide,
                    clientSide = release.ClientSide,
                    clientRequirement = release.ClientRequirement,
                    kind = release.Kind.ToString(),
                    dependencies = release.Dependencies
                }, WebUiProtocol.Json);
            }
            case "plugins.install":
            case "mods.install":
            {
                Select(parameters);
                var result = await client.SendAsync<ManagedContentOperationSnapshot>("BeginManagedContentInstall",
                    new BeginManagedContentInstallRequest(RequireServer(parameters).Definition.Id,
                        RequiredString(parameters, "projectId", 80),
                        RequiredString(parameters, "versionId", 80),
                        IncludeDependencies: false,
                        parameters["restartIfRunning"]?.GetValue<bool?>() ?? false,
                        Guid.TryParse(OptionalString(parameters, "operationId", 64), out var operationId)
                            ? operationId
                            : Guid.NewGuid())).ConfigureAwait(true);
                EnsureContentOperationObserver(result);
                return JsonSerializer.SerializeToNode(result, WebUiProtocol.Json);
            }
            case "plugins.plan":
            case "mods.plan":
            {
                Select(parameters);
                var result = await client.SendAsync<PluginInstallPlan>("PlanPluginProviderRelease",
                    new PluginProviderPlanRequest(RequireServer(parameters).Definition.Id,
                        RequiredString(parameters, "projectId", 80),
                        RequiredString(parameters, "versionId", 80))).ConfigureAwait(true);
                return JsonSerializer.SerializeToNode(result, WebUiProtocol.Json);
            }
            case "plugins.installPlan":
            case "mods.installPlan":
            {
                Select(parameters);
                var result = await client.SendAsync<ManagedContentOperationSnapshot>("BeginManagedContentInstall",
                    new BeginManagedContentInstallRequest(RequireServer(parameters).Definition.Id,
                        RequiredString(parameters, "projectId", 80),
                        RequiredString(parameters, "versionId", 80),
                        IncludeDependencies: true,
                        parameters["restartIfRunning"]?.GetValue<bool?>() ?? false,
                        Guid.TryParse(OptionalString(parameters, "operationId", 64), out var operationId)
                            ? operationId
                            : Guid.NewGuid())).ConfigureAwait(true);
                EnsureContentOperationObserver(result);
                return JsonSerializer.SerializeToNode(result, WebUiProtocol.Json);
            }
            case "content.operations":
            {
                Select(parameters);
                var result = await client.SendAsync<IReadOnlyList<ManagedContentOperationSnapshot>>(
                    "ManagedContentOperations",
                    new ManagedContentOperationsRequest(RequireServer(parameters).Definition.Id)).ConfigureAwait(true);
                foreach (var operation in result.Where(operation => !operation.IsTerminal))
                    EnsureContentOperationObserver(operation);
                return JsonSerializer.SerializeToNode(result, WebUiProtocol.Json);
            }
            case "content.cancel":
            {
                if (!Guid.TryParse(RequiredString(parameters, "operationId", 64), out var operationId))
                    throw new ArgumentException("A valid managed-content operation ID is required.");
                var result = await client.SendAsync<OperationResult>("CancelManagedContentOperation",
                    new ManagedContentOperationRequest(operationId)).ConfigureAwait(true);
                return JsonSerializer.SerializeToNode(result, WebUiProtocol.Json);
            }
            case "plugins.setEnabled":
            case "mods.setEnabled":
            {
                Select(parameters);
                var result = await client.SendAsync<OperationResult>("SetJarEnabled",
                    new JarEnabledRequest(RequireServer(parameters).Definition.Id,
                        RequiredString(parameters, "relativePath", 1024),
                        RequiredBool(parameters, "enabled"),
                        parameters["restartIfRunning"]?.GetValue<bool?>() ?? false)).ConfigureAwait(true);
                await viewModel.LoadWebUiInventoryAsync().ConfigureAwait(true);
                await bridge!.PublishSnapshotAsync().ConfigureAwait(true);
                return JsonSerializer.SerializeToNode(result, WebUiProtocol.Json);
            }
            case "plugins.remove":
            case "mods.remove":
            {
                Select(parameters);
                var result = await client.SendAsync<OperationResult>("RemoveJar",
                    new PluginRemoveRequest(RequireServer(parameters).Definition.Id,
                        RequiredString(parameters, "relativePath", 1024),
                        parameters["restartIfRunning"]?.GetValue<bool?>() ?? false)).ConfigureAwait(true);
                await viewModel.LoadWebUiInventoryAsync().ConfigureAwait(true);
                await bridge!.PublishSnapshotAsync().ConfigureAwait(true);
                return JsonSerializer.SerializeToNode(result, WebUiProtocol.Json);
            }
            case "plugins.configFiles":
            case "mods.configFiles":
                Select(parameters);
                return JsonSerializer.SerializeToNode(
                    await AddonConfigFilesAsync(
                        RequireServer(parameters).Definition.Id,
                        RequiredString(parameters, "relativePath", 1024)).ConfigureAwait(true),
                    WebUiProtocol.Json);
            case "plugins.saveConfig":
            case "mods.saveConfig":
            {
                Select(parameters);
                var content = parameters["file"]?.Deserialize<TextFileContent>(WebUiProtocol.Json)
                    ?? throw new ArgumentException("file is required.");
                if (content.RelativePath.Length is 0 or > 1024)
                    throw new ArgumentException("The configuration path is invalid.");
                var result = await client.SendAsync<OperationResult>("WriteAddonConfig",
                    new AddonConfigWriteRequest(
                        RequireServer(parameters).Definition.Id,
                        RequiredString(parameters, "addonRelativePath", 1024),
                        content,
                        parameters["restartIfRunning"]?.GetValue<bool?>() ?? false)).ConfigureAwait(true);
                return JsonSerializer.SerializeToNode(result, WebUiProtocol.Json);
            }
            case "console.send":
                Select(parameters);
                viewModel.ConsoleCommand = RequiredString(parameters, "command", 2048);
                await viewModel.SendConsoleCommandCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "workspace.load":
                await LoadWorkspaceAsync(parameters).ConfigureAwait(true);
                break;
            case "files.read":
                Select(parameters);
                return JsonSerializer.SerializeToNode(
                    await viewModel.ReadWebUiFileAsync(
                        RequireServer(parameters).Definition.Id,
                        RequiredString(parameters, "relativePath", 1024)).ConfigureAwait(true),
                    WebUiProtocol.Json);
            case "files.navigate":
                Select(parameters);
                await viewModel.LoadWebUiFolderAsync(
                    RequireServer(parameters).Definition.Id,
                    RequiredString(parameters, "relativePath", 1024)).ConfigureAwait(true);
                break;
            case "files.write":
                Select(parameters);
                var file = parameters["file"]?.Deserialize<TextFileContent>(WebUiProtocol.Json)
                    ?? throw new ArgumentException("file is required.");
                if (file.RelativePath.Length is 0 or > 1024)
                    throw new ArgumentException("The file path is invalid.");
                var writeResult = await viewModel.WriteWebUiFileAsync(
                    RequireServer(parameters).Definition.Id, file).ConfigureAwait(true);
                if (!writeResult.Success)
                    throw new InvalidOperationException(writeResult.Message);
                return JsonSerializer.SerializeToNode(writeResult, WebUiProtocol.Json);
            case "backups.create":
                Select(parameters);
                await viewModel.CreateBackupCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "backups.verify":
                Select(parameters);
                SelectBackup(parameters);
                await viewModel.VerifyBackupCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "backups.restore":
                Select(parameters);
                SelectBackup(parameters);
                await viewModel.RestoreBackupCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "players.moderate":
                await ModeratePlayerAsync(parameters).ConfigureAwait(true);
                break;
            case "players.addAllowlist":
                Select(parameters);
                viewModel.NewWhitelistPlayerName = RequiredString(parameters, "playerName", 16);
                if (!viewModel.AddWhitelistPlayerCommand.CanExecute(null))
                    throw new InvalidOperationException("Start the server before changing the allowlist.");
                await viewModel.AddWhitelistPlayerCommand.ExecuteAsync(null).ConfigureAwait(true);
                if (viewModel.HasAccessError)
                    throw new InvalidOperationException(viewModel.AccessErrorMessage);
                break;
            case "players.setWhitelist":
                Select(parameters);
                await viewModel.SetWhitelistEnabledAsync(RequiredBool(parameters, "enabled")).ConfigureAwait(true);
                if (viewModel.HasAccessError)
                    throw new InvalidOperationException(viewModel.AccessErrorMessage);
                break;
            case "schedules.upsert":
                Select(parameters);
                ApplySchedule(parameters);
                if (!viewModel.TryBuildWebUiSchedule(out var newSchedule, out var scheduleError))
                    throw new ArgumentException(scheduleError);
                var scheduleResult = await viewModel.SaveWebUiScheduleAsync(newSchedule).ConfigureAwait(true);
                if (!scheduleResult.Success)
                    throw new InvalidOperationException(scheduleResult.Message);
                break;
            case "schedules.delete":
                Select(parameters);
                var scheduleId = Guid.Parse(RequiredString(parameters, "scheduleId", 64));
                var schedule = viewModel.Schedules.FirstOrDefault(item => item.Id == scheduleId &&
                    item.ServerId == viewModel.SelectedServer?.Definition.Id)
                    ?? throw new ArgumentException("The schedule was not found for this server.");
                await viewModel.DeleteScheduleCommand.ExecuteAsync(schedule).ConfigureAwait(true);
                break;
            case "settings.saveGlobal":
                ApplyGlobalSettings(parameters);
                await viewModel.SaveSettingsCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "settings.saveServer":
                ApplyServerSettings(parameters);
                var propertiesChanged = viewModel.HasServerPropertyChanges;
                var memoryChanged = viewModel.HasMemoryChanges;
                if (propertiesChanged)
                {
                    await viewModel.SaveServerPropertiesCommand.ExecuteAsync(null).ConfigureAwait(true);
                    if (viewModel.HasServerPropertySaveError || viewModel.HasServerPropertyChanges)
                        throw new InvalidOperationException(viewModel.ServerPropertySaveError.Length > 0
                            ? viewModel.ServerPropertySaveError
                            : "The server settings were not confirmed by the authoritative settings service.");
                }
                if (memoryChanged)
                {
                    await viewModel.ApplyMemoryCommand.ExecuteAsync(null).ConfigureAwait(true);
                    if (viewModel.MemorySaveError.Length > 0 || viewModel.HasMemoryChanges)
                        throw new InvalidOperationException(viewModel.MemorySaveError.Length > 0
                            ? viewModel.MemorySaveError
                            : "The memory allocation was not confirmed by the authoritative settings service.");
                }
                var iconBase64 = parameters["iconPngBase64"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(iconBase64))
                {
                    try
                    {
                        await InstallAppearanceIconAsync(RequireServer(parameters), iconBase64).ConfigureAwait(true);
                    }
                    catch (Exception exception) when ((propertiesChanged || memoryChanged) &&
                        exception is IOException or InvalidDataException or InvalidOperationException or FormatException)
                    {
                        throw new InvalidOperationException(
                            $"The settings were saved, but the server icon could not be replaced: {exception.Message}", exception);
                    }
                }
                break;
            case "versions.check":
                Select(parameters);
                await viewModel.CheckForUpdatesCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "versions.install":
                Select(parameters);
                if (viewModel.CurrentUpdateCheck?.LatestVersion is null)
                    throw new InvalidOperationException("No installable update has been confirmed for this server.");
                return await BeginUpdateOperationAsync(parameters).ConfigureAwait(true);
            case "versions.rollback":
                Select(parameters);
                SelectVersion(parameters, requireRollbackReady: true);
                await viewModel.RollbackVersionCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "versions.verify":
                Select(parameters);
                SelectVersion(parameters, requireRollbackReady: false);
                await viewModel.VerifyVersionCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "versions.cancel":
                Select(parameters);
                if (viewModel.CurrentUpdateOperation is not { IsTerminal: false })
                    throw new InvalidOperationException("No cancellable update operation is active.");
                await viewModel.CancelPackUpdateCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.copyAddress":
                if (!TryServer(parameters, out var addressServer) || addressServer is null)
                    throw new ArgumentException("Select a server first.");
                var addressServerId = addressServer.Definition.Id;
                var storedRouter = viewModel.Dashboard.RouterMappings.FirstOrDefault(item =>
                    item.ServerId == addressServerId);
                var isSelectedAddressServer = viewModel.SelectedServer?.Definition.Id == addressServerId;
                var kind = RequiredString(parameters, "kind", 20).ToLowerInvariant();
                var address = kind switch
                {
                    "local" => $"localhost:{addressServer.Definition.Port}",
                    "lan" when !string.IsNullOrWhiteSpace(viewModel.Dashboard.Host.LanAddress) =>
                        $"{viewModel.Dashboard.Host.LanAddress}:{addressServer.Definition.Port}",
                    "public" when isSelectedAddressServer && viewModel.PublicAccessVerified =>
                        viewModel.PublicAccessVerifiedEndpoint,
                    "router" when isSelectedAddressServer && viewModel.RouterMapping.Enabled &&
                        viewModel.RouterMapping.HasRouterReportedAddress =>
                        viewModel.RouterMapping.RouterReportedEndpoint,
                    "router" when storedRouter is { HasActiveMapping: true,
                        RouterReportedExternalAddress.Length: > 0, ExternalPort: > 0 } =>
                        $"{storedRouter.RouterReportedExternalAddress}:{storedRouter.ExternalPort}",
                    "last" when isSelectedAddressServer && viewModel.ExternalReachability.CheckedAt is not null &&
                        viewModel.ExternalReachability.CheckedEndpoint.PublicAddress.Length > 0 &&
                        viewModel.ExternalReachability.CheckedEndpoint.ExternalPort > 0 =>
                        $"{viewModel.ExternalReachability.CheckedEndpoint.PublicAddress}:{viewModel.ExternalReachability.CheckedEndpoint.ExternalPort}",
                    "last" when storedRouter is { RouterReportedExternalAddress.Length: > 0, ExternalPort: > 0 } =>
                        $"{storedRouter.RouterReportedExternalAddress}:{storedRouter.ExternalPort}",
                    "router" => throw new InvalidOperationException("The active router mapping has not reported a likely public address."),
                    "public" => throw new InvalidOperationException("No outside-in check has verified a public address for this server."),
                    "lan" => throw new InvalidOperationException("ChunkPilot has not established a LAN address for this server."),
                    "last" => throw new InvalidOperationException("No previously checked Internet address is available for this server."),
                    _ => throw new ArgumentException("Address kind must be local, lan, router, last, or public.")
                };
                viewModel.CopyTextCommand.Execute(address);
                break;
            case "connectivity.open":
                Select(parameters);
                viewModel.NavigateServerDestinationCommand.Execute("Settings");
                break;
            case "connectivity.setMode":
                Select(parameters);
                if (!Enum.TryParse<NetworkMode>(RequiredString(parameters, "mode", 40), true, out var networkMode) ||
                    networkMode is not (NetworkMode.HomeNetwork or NetworkMode.PortForwarding))
                    throw new ArgumentException("Choose LAN or Internet hosting.");
                viewModel.SelectedNetworkMode = networkMode;
                await viewModel.SaveNetworkModeCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.router.check":
                Select(parameters);
                await viewModel.CheckDirectInternetCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.router.confirm":
                Select(parameters);
                if (!RequiredBool(parameters, "confirmed"))
                    throw new ArgumentException("Router setup requires deliberate confirmation.");
                await viewModel.ConfirmDirectInternetCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.router.cancelConsent":
                Select(parameters);
                viewModel.CancelDirectInternetConsentCommand.Execute(null);
                break;
            case "connectivity.router.stop":
                Select(parameters);
                if (!RequiredBool(parameters, "confirmed"))
                    throw new ArgumentException("Stopping Internet sharing requires deliberate confirmation.");
                await viewModel.TurnOffDirectInternetCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.router.cancel":
                Select(parameters);
                await viewModel.CancelDirectInternetOperationCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.router.retry":
                Select(parameters);
                await viewModel.RetryDirectInternetCleanupCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.external.check":
                Select(parameters);
                await viewModel.CheckExternalReachabilityCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.external.cancel":
                Select(parameters);
                await viewModel.CancelExternalReachabilityCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.firewall.primary":
                Select(parameters);
                await viewModel.ExecuteFirewallPrimaryActionCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.firewall.secondary":
                Select(parameters);
                await viewModel.ExecuteFirewallSecondaryActionCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.firewall.confirm":
                Select(parameters);
                if (!RequiredBool(parameters, "confirmed"))
                    throw new ArgumentException("Windows Firewall access requires deliberate confirmation.");
                await viewModel.ConfirmFirewallAccessCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.firewall.cancelConsent":
                Select(parameters);
                viewModel.CancelFirewallConsentCommand.Execute(null);
                break;
            case "connectivity.firewall.remove":
                Select(parameters);
                if (!RequiredBool(parameters, "confirmed"))
                    throw new ArgumentException("Removing Windows Firewall access requires deliberate confirmation.");
                await viewModel.RemoveFirewallAccessFromWebUiAsync().ConfigureAwait(true);
                break;
            case "connectivity.firewall.cancel":
                Select(parameters);
                await viewModel.CancelFirewallOperationCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "creation.catalog":
                return await CreationCatalogAsync(parameters).ConfigureAwait(true);
            case "creation.paperBuilds":
                return await CreationPaperBuildsAsync(parameters).ConfigureAwait(true);
            case "creation.loaderBuilds":
                return await CreationLoaderBuildsAsync(parameters).ConfigureAwait(true);
            case "creation.previewDestination":
                return await CreationDestinationAsync(parameters).ConfigureAwait(true);
            case "creation.chooseFolder":
                return ChooseCreationFolder(parameters);
            case "creation.begin":
                return BeginCreationOperation(parameters);
            case "creation.operations":
                return await CreationOperationsAsync().ConfigureAwait(true);
            case "creation.progress":
                return await CreationProgressAsync(parameters).ConfigureAwait(true);
            case "creation.cancel":
                return await CancelCreationAsync(parameters).ConfigureAwait(true);
            default:
                throw new ArgumentException($"The bridge method '{method}' is not allowed.");
        }
        if (RequiresFullPresentationRefresh(method))
            await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        await bridge!.PublishSnapshotAsync().ConfigureAwait(true);
        return Accepted(method);
    }

    private JsonNode? BeginLifecycleOperation(string method, ServerSnapshot server)
    {
        var serverId = server.Definition.Id;
        if (lifecycleOperations.TryGetValue(serverId, out var active) && !active.Task.IsCompleted)
        {
            if (!string.Equals(active.Method, method, StringComparison.Ordinal))
                throw new InvalidOperationException($"{active.Method.Replace("servers.", "", StringComparison.OrdinalIgnoreCase)} is already in progress for this server.");
            return JsonSerializer.SerializeToNode(new
            {
                accepted = true,
                operationId = active.OperationId,
                method,
                duplicate = true
            }, WebUiProtocol.Json);
        }

        var operationId = Guid.NewGuid();
        var task = viewModel.RunWebUiLifecycleAsync(method, server);
        lifecycleOperations[serverId] = new(operationId, method, task);
        _ = ObserveLifecycleResultAsync(serverId, operationId, method, task);
        _ = bridge?.PublishSnapshotAsync();
        return JsonSerializer.SerializeToNode(new
        {
            accepted = true,
            operationId,
            method,
            duplicate = false
        }, WebUiProtocol.Json);
    }

    private JsonNode? BeginDeletionOperation(ServerSnapshot server, ServerDeletionRequest request)
    {
        const string method = "servers.delete";
        var serverId = server.Definition.Id;
        if (lifecycleOperations.TryGetValue(serverId, out var active) && !active.Task.IsCompleted)
        {
            if (!string.Equals(active.Method, method, StringComparison.Ordinal))
                throw new InvalidOperationException($"{active.Method.Replace("servers.", "", StringComparison.OrdinalIgnoreCase)} is already in progress for this server.");
            return JsonSerializer.SerializeToNode(new
            {
                accepted = true,
                operationId = active.OperationId,
                method,
                duplicate = true
            }, WebUiProtocol.Json);
        }

        var operationId = Guid.NewGuid();
        var task = client.SendAsync<ServerDeletionReceipt>("DeleteServer", request);
        lifecycleOperations[serverId] = new(operationId, method, task);
        _ = ObserveLifecycleOperationAsync(serverId, operationId, method, task);
        return JsonSerializer.SerializeToNode(new
        {
            accepted = true,
            operationId,
            method,
            duplicate = false
        }, WebUiProtocol.Json);
    }

    private JsonNode? BeginManagedCopyOperation(ServerSnapshot server, ManagedCopyConversionRequest request)
    {
        const string method = "servers.createManagedCopy";
        var serverId = server.Definition.Id;
        if (lifecycleOperations.TryGetValue(serverId, out var active) && !active.Task.IsCompleted)
        {
            if (!string.Equals(active.Method, method, StringComparison.Ordinal))
                throw new InvalidOperationException($"{active.Method.Replace("servers.", "", StringComparison.OrdinalIgnoreCase)} is already in progress for this server.");
            return JsonSerializer.SerializeToNode(new
            {
                accepted = true, operationId = active.OperationId, method, duplicate = true
            }, WebUiProtocol.Json);
        }

        var operationId = Guid.NewGuid();
        var task = client.SendAsync<ManagedCopyConversionReceipt>("CreateManagedCopy", request);
        lifecycleOperations[serverId] = new(operationId, method, task);
        _ = ObserveLifecycleOperationAsync(serverId, operationId, method, task);
        return JsonSerializer.SerializeToNode(new
        {
            accepted = true, operationId, method, duplicate = false
        }, WebUiProtocol.Json);
    }

    private async Task ObserveLifecycleOperationAsync(Guid serverId, Guid operationId, string method, Task task)
    {
        string? error = null;
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            error = SecretRedactor.Redact(exception.Message);
        }
        finally
        {
            if (lifecycleOperations.TryGetValue(serverId, out var active) && active.OperationId == operationId)
                lifecycleOperations.Remove(serverId);
        }

        try
        {
            await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
            if (error is null && method == "servers.delete")
                viewModel.NavigateCommand.Execute("Servers");
            if (bridge is { } current)
            {
                await current.PublishSnapshotAsync().ConfigureAwait(true);
                current.PublishOperationCompleted(operationId, method, serverId, error is null, error);
            }
        }
        catch (Exception exception)
        {
            bridge?.PublishOperationCompleted(operationId, method, serverId, false,
                SecretRedactor.Redact(error ?? exception.Message));
        }
    }

    private async Task ObserveLifecycleResultAsync(
        Guid serverId,
        Guid operationId,
        string method,
        Task<OperationResult> task)
    {
        string? error = null;
        try
        {
            var result = await task.ConfigureAwait(true);
            if (!result.Success)
                error = SecretRedactor.Redact(result.Message);
        }
        catch (Exception exception)
        {
            error = SecretRedactor.Redact(exception.Message);
        }
        finally
        {
            if (lifecycleOperations.TryGetValue(serverId, out var active) && active.OperationId == operationId)
                lifecycleOperations.Remove(serverId);
        }

        try
        {
            await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
            if (bridge is { } current)
            {
                await current.PublishSnapshotAsync().ConfigureAwait(true);
                current.PublishOperationCompleted(operationId, method, serverId, error is null, error);
            }
        }
        catch (Exception exception)
        {
            bridge?.PublishOperationCompleted(operationId, method, serverId, false,
                SecretRedactor.Redact(error ?? exception.Message));
        }
    }

    private sealed record LifecycleWebUiOperation(Guid OperationId, string Method, Task Task);

    private async Task<JsonNode?> BeginUpdateOperationAsync(JsonObject parameters)
    {
        const string method = "versions.install";
        var server = viewModel.SelectedServer ??
                     throw new InvalidOperationException("No server is selected.");
        var check = viewModel.CurrentUpdateCheck ??
                    throw new InvalidOperationException("No authoritative update check is available.");
        if (check.Status != ServerUpdateStatus.UpdateAvailable)
            throw new InvalidOperationException(check.Status == ServerUpdateStatus.UpToDate
                ? "This exact pack release is already installed."
                : "No installable pack update is currently available.");
        var target = check.LatestVersion ??
                     throw new InvalidOperationException("No installable update has been confirmed for this server.");
        if (check.Compatibility is UpdateCompatibility.Incompatible or UpdateCompatibility.Unknown)
            throw new InvalidOperationException(check.CompatibilityReasons.Count == 0
                ? "This update is not compatible with the selected server."
                : string.Join(Environment.NewLine, check.CompatibilityReasons));

        var requestedOperationId = parameters["operationId"]?.GetValue<Guid?>() ?? Guid.NewGuid();
        var started = await client.SendAsync<UpdateOperationRequest>("BeginPackUpdate", new UpdateInstallRequest
        {
            OperationId = requestedOperationId,
            ServerId = server.Definition.Id,
            TargetVersion = target,
            PlayerCountdownSeconds = server.State == ServerState.Running ? 30 : 0,
            StartForValidation = true
        }).ConfigureAwait(true);
        EnsureUpdateOperationObserver(started.OperationId, server.Definition.Id);
        return JsonSerializer.SerializeToNode(new
        {
            accepted = true,
            operationId = started.OperationId,
            method
        }, WebUiProtocol.Json);
    }

    private void EnsureUpdateOperationObserver(Guid operationId, Guid serverId)
    {
        if (!observedUpdateOperations.Add(operationId))
            return;
        _ = ObserveUpdateOperationAsync(operationId, serverId);
    }

    private async Task ObserveUpdateOperationAsync(Guid operationId, Guid serverId)
    {
        UpdateOperationSnapshot? terminal = null;
        string? observerError = null;
        try
        {
            while (!closed)
            {
                var current = await client.SendAsync<UpdateOperationSnapshot>("GetPackUpdate",
                    new UpdateOperationRequest(operationId)).ConfigureAwait(true);
                viewModel.CurrentUpdateOperation = current;
                if (bridge is { } currentBridge)
                    await currentBridge.PublishSnapshotAsync().ConfigureAwait(true);
                if (current.IsTerminal)
                {
                    terminal = current;
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            observerError = SecretRedactor.Redact(exception.Message);
        }
        finally
        {
            observedUpdateOperations.Remove(operationId);
        }

        if (closed)
            return;
        try
        {
            await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
            await viewModel.LoadUpdateDetailsAsync().ConfigureAwait(true);
            if (bridge is not { } currentBridge)
                return;
            await currentBridge.PublishSnapshotAsync().ConfigureAwait(true);
            currentBridge.PublishOperationCompleted(operationId, "versions.install", serverId,
                terminal?.Success is true && observerError is null,
                observerError ?? (terminal?.Success is false ? terminal.Error : null));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            bridge?.PublishOperationCompleted(operationId, "versions.install", serverId, false,
                SecretRedactor.Redact(observerError ?? exception.Message));
        }
    }

    private void EnsureContentOperationObserver(ManagedContentOperationSnapshot operation)
    {
        if (operation.IsTerminal || !observedContentOperations.Add(operation.OperationId))
            return;
        _ = ObserveContentOperationAsync(operation.OperationId, operation.ServerId, operation.Kind);
    }

    private async Task ObserveContentOperationAsync(
        Guid operationId,
        Guid serverId,
        ManagedContentOperationKind kind)
    {
        ManagedContentOperationSnapshot? terminal = null;
        string? observerError = null;
        try
        {
            while (!closed)
            {
                var current = await client.SendAsync<ManagedContentOperationSnapshot>(
                    "ManagedContentOperation", new ManagedContentOperationRequest(operationId)).ConfigureAwait(true);
                if (current.IsTerminal)
                {
                    terminal = current;
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            observerError = SecretRedactor.Redact(exception.Message);
        }
        finally
        {
            observedContentOperations.Remove(operationId);
        }

        if (closed)
            return;
        try
        {
            await viewModel.LoadWebUiInventoryAsync().ConfigureAwait(true);
            if (bridge is not { } currentBridge)
                return;
            await currentBridge.PublishSnapshotAsync().ConfigureAwait(true);
            var method = kind is ManagedContentOperationKind.InstallPack or ManagedContentOperationKind.UpdatePack
                ? "packs.operation"
                : "content.operation";
            currentBridge.PublishOperationCompleted(operationId, method, serverId,
                terminal?.Success is true && observerError is null,
                observerError ?? terminal?.Error);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            bridge?.PublishOperationCompleted(operationId, "content.operation", serverId, false,
                SecretRedactor.Redact(observerError ?? exception.Message));
        }
    }

    private sealed class CreationWebUiOperation(Guid operationId, CancellationTokenSource cancellation)
    {
        public Guid OperationId { get; } = operationId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<Guid>? Registration { get; set; }
        public string? Error { get; set; }
    }

    private async Task LoadWorkspaceAsync(JsonObject parameters)
    {
        Select(parameters);
        var destination = RequiredString(parameters, "destination", 40).ToLowerInvariant();
        switch (destination)
        {
            case "console":
                viewModel.NavigateServerDestinationCommand.Execute("Console");
                break;
            case "players":
                viewModel.NavigateServerDestinationCommand.Execute("Access");
                break;
            case "files":
                await viewModel.LoadFilesCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "content":
                await viewModel.LoadWebUiInventoryAsync().ConfigureAwait(true);
                await viewModel.LoadUpdateDetailsAsync().ConfigureAwait(true);
                break;
            case "backups":
                viewModel.NavigateServerDestinationCommand.Execute("Protection");
                break;
            case "versions":
                await viewModel.LoadUpdateDetailsAsync().ConfigureAwait(true);
                break;
        }
    }

    private async Task<IReadOnlyList<WebUiPluginConfigFile>> AddonConfigFilesAsync(
        Guid serverId,
        string addonRelativePath)
    {
        var addon = viewModel.Inventory.FirstOrDefault(item =>
            item.RelativePath.Equals(addonRelativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected add-on is no longer in the current inventory.");
        var server = viewModel.SelectedServer?.Definition ??
                     throw new InvalidOperationException("No server is selected.");
        var isMod = server.Ecosystem is ServerEcosystem.Fabric or ServerEcosystem.Quilt or
            ServerEcosystem.Forge or ServerEcosystem.NeoForge;
        var candidateNames = new[] { addon.Id, addon.Name }
            .Where(IsSafeConfigDirectoryName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        var results = new List<WebUiPluginConfigFile>();
        foreach (var candidate in candidateNames)
        {
            var folder = Path.Combine(isMod ? "config" : "plugins", candidate);
            try
            {
                var entries = await client.SendAsync<IReadOnlyList<FileSystemEntry>>(
                    "ListFiles", new FilesRequest(serverId, folder)).ConfigureAwait(true);
                results.AddRange(entries
                    .Where(entry => !entry.IsDirectory && IsSupportedConfigFile(entry.Name))
                    .Take(250)
                    .Select(entry => new WebUiPluginConfigFile(
                        entry.RelativePath,
                        entry.Name,
                        entry.SizeBytes,
                        entry.ModifiedAt,
                        Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant())));
            }
            catch (Exception exception) when (
                exception is DirectoryNotFoundException or FileNotFoundException or InvalidOperationException or IOException)
            {
                // An add-on is not required to use a same-named configuration directory.
                // Unknown ownership is deliberately not guessed or recursively scanned.
            }
        }
        if (isMod)
        {
            try
            {
                var entries = await client.SendAsync<IReadOnlyList<FileSystemEntry>>(
                    "ListFiles", new FilesRequest(serverId, "config")).ConfigureAwait(true);
                var exactNames = candidateNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
                results.AddRange(entries
                    .Where(entry => !entry.IsDirectory && IsSupportedConfigFile(entry.Name) &&
                                    exactNames.Contains(Path.GetFileNameWithoutExtension(entry.Name)))
                    .Take(32)
                    .Select(entry => new WebUiPluginConfigFile(
                        entry.RelativePath,
                        entry.Name,
                        entry.SizeBytes,
                        entry.ModifiedAt,
                        Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant())));
            }
            catch (Exception exception) when (
                exception is DirectoryNotFoundException or FileNotFoundException or InvalidOperationException or IOException)
            {
                // A mod need not have a top-level exact-ID configuration file.
            }
        }
        return results.DistinctBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSafeConfigDirectoryName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 120 &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
        value is not "." and not "..";

    private static bool IsSupportedConfigFile(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".yml" or ".yaml" or ".json" or ".jsonc" or ".toml" or ".properties" or ".conf";

    private sealed record WebUiPluginConfigFile(
        string RelativePath,
        string Name,
        long SizeBytes,
        DateTimeOffset ModifiedAt,
        string Format);

    private async Task<JsonNode?> ChooseLocalAddonAsync(JsonObject parameters)
    {
        Select(parameters);
        var serverId = RequireServer(parameters).Definition.Id;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = RequireServer(parameters).Definition.Ecosystem is ServerEcosystem.Fabric or ServerEcosystem.Quilt or
                ServerEcosystem.Forge or ServerEcosystem.NeoForge
                ? "Choose a local server mod"
                : "Choose a local Paper plugin",
            Filter = "Minecraft add-on (*.jar)|*.jar",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return JsonSerializer.SerializeToNode(new { cancelled = true }, WebUiProtocol.Json);
        var preview = await client.SendAsync<ModPluginEntry>("InspectJar",
            new JarInstallRequest(serverId, dialog.FileName)).ConfigureAwait(true);
        var selection = localPluginTokens.Issue(serverId, dialog.FileName);
        return JsonSerializer.SerializeToNode(new
        {
            cancelled = false,
            token = selection.Token,
            fileName = selection.FileName,
            expiresAt = selection.ExpiresAt,
            plugin = new
            {
                name = preview.Name,
                version = preview.Version,
                id = preview.Id,
                loader = preview.Loader,
                sizeBytes = preview.SizeBytes,
                dependencies = preview.Dependencies,
                compatibility = preview.Compatibility.ToString(),
                compatibilityReason = preview.CompatibilityReason,
                clientRequirement = preview.ClientRequirement
            }
        }, WebUiProtocol.Json);
    }

    private string ConsumeLocalPlugin(string token, Guid serverId)
    {
        return localPluginTokens.Consume(serverId, token);
    }

    private void ApplySchedule(JsonObject parameters)
    {
        viewModel.ScheduleName = RequiredString(parameters, "name", 120);
        if (!Enum.TryParse<ScheduledAction>(RequiredString(parameters, "action", 40), true, out var action))
            throw new ArgumentException("The scheduled action is invalid.");
        if (!Enum.TryParse<ScheduleKind>(RequiredString(parameters, "kind", 40), true, out var kind))
            throw new ArgumentException("The schedule kind is invalid.");
        viewModel.ScheduleAction = action;
        viewModel.ScheduleKind = kind;
        viewModel.ScheduleIntervalMinutes = RequiredInt(parameters, "intervalMinutes", 1, 525_600, 1440);
        viewModel.ScheduleAt = RequiredString(parameters, "at", 80);
        viewModel.ScheduleCron = parameters["cron"]?.GetValue<string>()?.Trim() ?? "";
        viewModel.ScheduleCommand = parameters["command"]?.GetValue<string>()?.Trim() ?? "";
        if (viewModel.ScheduleCron.Length > 160 || viewModel.ScheduleCommand.Length > 2048)
            throw new ArgumentException("The schedule details are too long.");
        viewModel.RestartCountdownSeconds = RequiredInt(parameters, "restartCountdownSeconds", 0, 3600, 60);
        viewModel.BackupBeforeRestart = parameters["backupBeforeRestart"]?.GetValue<bool?>() ?? false;
    }

    private async Task<JsonNode?> LoadModpackVersionsAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CatalogProvider>(RequiredString(parameters, "provider", 32), true,
                out var provider) || provider is not (CatalogProvider.Modrinth or CatalogProvider.CurseForge))
            throw new ArgumentException("The modpack provider is invalid.");
        var result = await client.SendAsync<CatalogVersionInventory>(
            "CatalogProviderVersions",
            new CatalogVersionInventoryRequest(
                provider,
                parameters["cacheOnly"]?.GetValue<bool?>() ?? false),
            cancellationToken).ConfigureAwait(true);
        return JsonSerializer.SerializeToNode(new
        {
            provider = result.Provider.ToString(),
            state = result.State.ToString(),
            versions = result.Versions.Select(version => new
            {
                versionId = version.VersionId,
                kind = version.Kind.ToString(),
                version.PublishedAt,
                version.IsMajor
            }).ToArray(),
            result.Detail,
            result.FailedStage,
            result.RetrievedAt,
            result.FromCache,
            result.Stale
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> SearchModpacksAsync(
        JsonObject parameters,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CatalogProvider>(RequiredString(parameters, "provider", 32), true,
                out var provider) || provider is not (CatalogProvider.Modrinth or CatalogProvider.CurseForge))
            throw new ArgumentException("The modpack provider is invalid.");
        var search = OptionalString(parameters, "search", 120);
        var minecraft = OptionalString(parameters, "minecraftVersion", 40);
        var loader = OptionalString(parameters, "loader", 40);
        var category = OptionalString(parameters, "category", 60);
        var sort = Enum.TryParse<CatalogSort>(OptionalString(parameters, "sort", 32), true, out var parsedSort)
            ? parsedSort
            : CatalogSort.Updated;
        var query = new CatalogQuery
        {
            Provider = provider,
            Search = search,
            MinecraftVersion = minecraft,
            Loader = loader,
            Category = category,
            MaximumChannel = parameters["includeExperimental"]?.GetValue<bool?>() == true
                ? ReleaseChannel.Alpha
                : ReleaseChannel.Stable,
            ServerPackRequired = true,
            ExcludeClientOnly = true,
            Limit = RequiredInt(parameters, "limit", 1, 20, 20),
            Sort = sort
        };
        var result = await client.SendAsync<CatalogBrowseResult>(
            cacheOnly ? "BrowseCatalogCache" : "BrowseCatalogDetailed", query, cancellationToken).ConfigureAwait(true);
        foreach (var item in result.Items)
            modpackCatalog[CatalogKey(item.Provider, item.ProjectId)] = item;
        return JsonSerializer.SerializeToNode(new
        {
            provider = result.Provider.ToString(),
            state = result.State.ToString(),
            items = result.Items.Select(ToWebModpackProject).ToArray(),
            result.Detail,
            result.FailedStage,
            result.RetrievedAt,
            result.FromCache,
            result.Stale
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> ResolveModpackLinkAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var reference = ProviderLinkParser.Parse(RequiredString(parameters, "url", 2048));
        if (reference.Provider == CatalogProvider.CurseForge)
            throw new InvalidOperationException(
                "CurseForge integration is being activated for ChunkPilot. Modrinth links and local pack imports are available now.");

        var query = new CatalogQuery
        {
            Provider = reference.Provider,
            Search = reference.ProjectReference,
            MaximumChannel = reference.Kind == ProviderLinkKind.ExactRelease
                ? ReleaseChannel.Alpha
                : ReleaseChannel.Stable,
            ServerPackRequired = true,
            ExcludeClientOnly = true,
            Limit = 20,
            Sort = CatalogSort.Relevance
        };
        var result = await client.SendAsync<CatalogBrowseResult>(
            "BrowseCatalogDetailed", query, cancellationToken).ConfigureAwait(true);
        if (result.State is CatalogLoadState.AuthenticationRequired or CatalogLoadState.RateLimited or CatalogLoadState.Failed)
            throw new InvalidOperationException(result.Detail);

        var item = result.Items.FirstOrDefault(candidate =>
                       candidate.ProjectId.Equals(reference.ProjectReference, StringComparison.OrdinalIgnoreCase) ||
                       candidate.Slug.Equals(reference.ProjectReference, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("The provider project could not be resolved from that link.");
        var release = reference.ReleaseReference is { } exact
            ? item.Versions.FirstOrDefault(candidate =>
                candidate.VersionId.Equals(exact, StringComparison.OrdinalIgnoreCase))
            : CatalogPolicy.SelectDefaultVersion(item, query);
        if (release is null)
            throw new InvalidOperationException(reference.Kind == ProviderLinkKind.ExactRelease
                ? "That exact provider release is not a server-capable pack release."
                : "The project has no stable server-capable release that ChunkPilot can install.");

        modpackCatalog[CatalogKey(item.Provider, item.ProjectId)] = item;
        return JsonSerializer.SerializeToNode(new
        {
            reference.CanonicalUrl,
            exactRelease = reference.Kind == ProviderLinkKind.ExactRelease,
            project = ToWebModpackProject(item),
            release = ToWebModpackRelease(item, release),
            detail = reference.Kind == ProviderLinkKind.ExactRelease
                ? "Resolved the exact release from the provider link."
                : "Selected the newest stable server-capable release."
        }, WebUiProtocol.Json);
    }

    private static object ToWebModpackProject(CatalogItem item) => new
        {
            provider = item.Provider.ToString(),
            item.ProjectId,
            item.Slug,
            item.Name,
            item.Author,
            item.Summary,
            item.DownloadCount,
            item.UpdatedAt,
            item.Categories,
            hasImage = !string.IsNullOrWhiteSpace(item.IconUrl),
            serverSupport = item.InstallationSupport.ToString(),
            clientRequirement = item.ClientRequirement.ToString(),
            trend = new { available = false, detail = "No local period snapshot history exists yet." },
            versions = item.Versions.Select(version => ToWebModpackRelease(item, version)).ToArray()
        };

    private static object ToWebModpackRelease(CatalogItem item, CatalogVersion version)
    {
        var canCreate = item.Provider == CatalogProvider.Modrinth &&
                        version.HasServerPackage && version.Sha1.Length == 40 &&
                        version.Sha512.Length == 128 && version.SizeBytes is > 0;
        var limitation = canCreate ? "" : item.Provider == CatalogProvider.CurseForge
            ? "CurseForge integration is being activated for ChunkPilot. Import a local server pack or use Modrinth for now."
            : !version.HasServerPackage
                ? "This release does not expose a dedicated server package."
                : "This release is missing the complete integrity metadata required for managed creation.";
        return new
        {
            version.VersionId,
            version.VersionName,
            version.MinecraftVersion,
            version.Loader,
            releaseChannel = version.ReleaseChannel.ToString(),
            version.PublishedAt,
            version.SizeBytes,
            version.Changelog,
            version.RequiredJavaMajor,
            hasIntegrity = version.Sha1.Length == 40 &&
                           (item.Provider == CatalogProvider.CurseForge || version.Sha512.Length == 128),
            canCreate,
            limitation
        };
    }

    private static string CatalogKey(CatalogProvider provider, string projectId) =>
        $"{provider}:{projectId}";

    private async Task<JsonNode?> LoadModpackImageAsync(JsonObject parameters)
    {
        if (!Enum.TryParse<CatalogProvider>(RequiredString(parameters, "provider", 32), true,
                out var provider) || provider is not (CatalogProvider.Modrinth or CatalogProvider.CurseForge))
            throw new ArgumentException("The modpack provider is invalid.");
        var projectId = RequiredString(parameters, "projectId", 80);
        if (!modpackCatalog.TryGetValue(CatalogKey(provider, projectId), out var item) ||
            string.IsNullOrWhiteSpace(item.IconUrl))
            return JsonSerializer.SerializeToNode(new { dataUrl = (string?)null }, WebUiProtocol.Json);
        if (modpackImageCache.TryGetValue(item.IconUrl, out var cached))
            return JsonSerializer.SerializeToNode(new { dataUrl = cached }, WebUiProtocol.Json);
        if (!Uri.TryCreate(item.IconUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(provider == CatalogProvider.Modrinth && uri.IdnHost.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) ||
              provider == CatalogProvider.CurseForge && uri.IdnHost.Equals("media.forgecdn.net", StringComparison.OrdinalIgnoreCase)))
            return JsonSerializer.SerializeToNode(new { dataUrl = (string?)null }, WebUiProtocol.Json);
        using var response = await modpackImages.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (mediaType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new InvalidDataException("The provider image did not use a supported image format.");
        if (response.Content.Headers.ContentLength is > 524_288)
            throw new InvalidDataException("The provider image exceeds ChunkPilot's 512 KB cache limit.");
        await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(true);
        using var raw = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer).ConfigureAwait(true);
            if (count == 0) break;
            if (raw.Length + count > 524_288)
                throw new InvalidDataException("The provider image exceeds ChunkPilot's 512 KB cache limit.");
            raw.Write(buffer, 0, count);
        }
        raw.Position = 0;
        var info = await ImageSharpImage.IdentifyAsync(raw).ConfigureAwait(true)
            ?? throw new InvalidDataException("The provider image could not be decoded.");
        if (info.Width is <= 0 or > 4096 || info.Height is <= 0 or > 4096 ||
            (long)info.Width * info.Height > 16_777_216)
            throw new InvalidDataException("The provider image dimensions exceed ChunkPilot's safe preview limit.");
        raw.Position = 0;
        using var image = await ImageSharpImage.LoadAsync<Rgba32>(raw).ConfigureAwait(true);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new ImageSharpSize(160, 160),
            Mode = ImageSharpResizeMode.Max,
            Sampler = KnownResamplers.Lanczos3
        }));
        using var encoded = new MemoryStream();
        await image.SaveAsync(encoded, new PngEncoder()).ConfigureAwait(true);
        var dataUrl = $"data:image/png;base64,{Convert.ToBase64String(encoded.ToArray())}";
        if (modpackImageCache.Count >= 32)
            modpackImageCache.Remove(modpackImageCache.Keys.First());
        modpackImageCache[item.IconUrl] = dataUrl;
        return JsonSerializer.SerializeToNode(new { dataUrl }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> ChooseLocalServerImportAsync(JsonObject parameters)
    {
        var requestedKind = OptionalString(parameters, "kind", 16);
        string selectedPath;
        if (requestedKind.Equals("folder", StringComparison.OrdinalIgnoreCase))
        {
            selectedPath = new DialogService().SelectFolder("Choose a complete Minecraft server folder") ?? "";
            if (string.IsNullOrWhiteSpace(selectedPath))
                return JsonSerializer.SerializeToNode(new { cancelled = true }, WebUiProtocol.Json);
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a server ZIP, modpack, or server JAR",
                Filter = "Supported server packages (*.zip;*.mrpack;*.jar)|*.zip;*.mrpack;*.jar|Server ZIP (*.zip)|*.zip|Modrinth packs (*.mrpack)|*.mrpack|Server JAR (*.jar)|*.jar",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true)
                return JsonSerializer.SerializeToNode(new { cancelled = true }, WebUiProtocol.Json);
            selectedPath = dialog.FileName;
        }
        var inspection = await client.SendAsync<ServerImportInspection>("InspectServerImport",
            new ServerImportInspectRequest(selectedPath)).ConfigureAwait(true);
        var token = localImportTokens.Issue(selectedPath, inspection);
        return JsonSerializer.SerializeToNode(new
        {
            cancelled = false,
            token.Token,
            token.FileName,
            token.ExpiresAt,
            inspection = new
            {
                sourceKind = inspection.SourceKind.ToString(),
                name = inspection.DisplayName,
                summary = inspection.SourceKind == ServerImportSourceKind.ServerFolder
                    ? "Complete server folder reviewed without modifying its files."
                    : "Local package reviewed without executing any included code.",
                inspection.MinecraftVersion,
                loader = inspection.Platform,
                inspection.LoaderVersion,
                inspection.RequiredJavaMajor,
                requiredServerFiles = inspection.FileCount,
                optionalServerFiles = 0,
                excludedClientFiles = 0,
                indexedServerBytes = inspection.ExpandedSizeBytes,
                inspection.SourceSizeBytes,
                inspection.ExpandedSizeBytes,
                inspection.FileCount,
                inspection.ModCount,
                inspection.PluginCount,
                inspection.ContainsWorld,
                inspection.ServerRoot,
                inspection.LaunchCandidates,
                inspection.CanReference,
                canCreate = inspection.CanInstall,
                inspection.Limitation
            }
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> ChooseLegacyServerArtifactAsync(JsonObject parameters)
    {
        creationCatalog ??= await creation.GetCatalogAsync(true, false).ConfigureAwait(true);
        var versionId = RequiredString(parameters, "versionId", 80);
        var version = creationCatalog.Options.FirstOrDefault(item =>
            item.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Choose an exact Minecraft version before selecting server files.");
        if (version.HasServerDownload)
            throw new ArgumentException("This version already has an official Mojang server download; user-supplied files are not needed.");
        if (version.RequiredJavaMajor is null || !version.LaunchProfile.IsResolved)
            throw new ArgumentException(
                "ChunkPilot has not established a safe Java and launch profile for this historical version.");
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose your Minecraft {version.VersionId} dedicated-server JAR",
            Filter = "Minecraft server JAR (*.jar)|*.jar",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return JsonSerializer.SerializeToNode(new { cancelled = true }, WebUiProtocol.Json);
        var inspected = await new LegacyServerArtifactInspector().InspectAsync(
            dialog.FileName, version.VersionId, version.ServerSha1).ConfigureAwait(true);
        var token = legacyArtifactTokens.Issue(inspected);
        return JsonSerializer.SerializeToNode(new
        {
            cancelled = false,
            token.Token,
            token.FileName,
            token.MinecraftVersion,
            token.SizeBytes,
            token.Sha256,
            token.MatchesOfficialHash,
            token.IdentityEvidence,
            token.ExpiresAt
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> CreationCatalogAsync(JsonObject parameters)
    {
        var forceRefresh = parameters["forceRefresh"]?.GetValue<bool?>() ?? false;
        var platform = OptionalString(parameters, "platform", 20);
        if (platform.Equals("Paper", StringComparison.OrdinalIgnoreCase))
            return await PaperCreationCatalogAsync(forceRefresh).ConfigureAwait(true);
        if (TryLoaderPlatform(platform, out var loaderPlatform))
            return await LoaderCreationCatalogAsync(loaderPlatform, forceRefresh).ConfigureAwait(true);
        creationCatalog = await creation.GetCatalogAsync(true, forceRefresh).ConfigureAwait(true);
        return JsonSerializer.SerializeToNode(new
        {
            available = creationCatalog.ProviderAvailable,
            message = creationCatalog.UnavailableDetail,
            fromCache = creationCatalog.IsFromCache,
            stale = creationCatalog.IsStale,
            retrievedAt = creationCatalog.RetrievedUtc,
            manifestLatestReleaseId = creationCatalog.ManifestLatestReleaseId,
            manifestLatestSnapshotId = creationCatalog.ManifestLatestSnapshotId,
            latestVerifiedReleaseId = creationCatalog.LatestVerifiedReleaseId,
            versions = creationCatalog.Options.Select(option => new
            {
                id = option.VersionId,
                label = option.VersionId,
                channel = option.Channel.ToString(),
                releaseKind = option.ReleaseKind.ToString(),
                releaseTime = option.ReleaseTime,
                javaMajor = option.RequiredJavaMajor,
                javaSource = option.JavaRequirementSource.ToString(),
                support = option.SupportTier.ToString(),
                supportReason = option.SupportReason,
                selectable = option.IsSelectable,
                hasServerArtifact = option.HasServerDownload,
                artifactSize = option.ServerSizeBytes,
                hasIntegrityMetadata = !string.IsNullOrWhiteSpace(option.ServerSha1) && option.ServerSizeBytes is > 0,
                launchProfile = new
                {
                    kind = option.LaunchProfile.Kind.ToString(),
                    arguments = option.LaunchProfile.Arguments,
                    requiresEulaFile = option.LaunchProfile.RequiresEulaFile,
                    evidence = option.LaunchProfile.Evidence
                },
                capabilities = option.LaunchProfile.Capabilities,
                certification = option.Certification,
                warnings = option.Warnings,
                evidence = option.CertificationEvidence,
                provenance = option.Provenance
            })
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> PaperCreationCatalogAsync(bool forceRefresh)
    {
        paperVersionCatalog = await paperCreation.GetVersionsAsync(forceRefresh).ConfigureAwait(true);
        return JsonSerializer.SerializeToNode(new
        {
            platform = "Paper",
            available = paperVersionCatalog.ProviderAvailable,
            message = paperVersionCatalog.UnavailableDetail,
            fromCache = paperVersionCatalog.IsFromCache,
            stale = paperVersionCatalog.IsStale,
            retrievedAt = paperVersionCatalog.RetrievedUtc,
            manifestLatestReleaseId = "",
            manifestLatestSnapshotId = "",
            latestVerifiedReleaseId = paperVersionCatalog.Versions.FirstOrDefault(option =>
                option.SupportTier is MinecraftVersionSupportTier.Recommended or MinecraftVersionSupportTier.Verified)?.VersionId ?? "",
            versions = paperVersionCatalog.Versions.Select(option => new
            {
                id = option.VersionId,
                label = option.VersionId,
                channel = option.ReleaseKind == MinecraftReleaseKind.Release ? "Stable" : "Snapshot",
                releaseKind = option.ReleaseKind.ToString(),
                releaseTime = (DateTimeOffset?)null,
                javaMajor = option.RequiredJavaMajor,
                javaSource = "ChunkPilotPolicy",
                support = option.IsSelectable ? option.SupportTier.ToString() : "Unavailable",
                supportReason = option.SupportReason,
                selectable = option.IsSelectable,
                hasServerArtifact = false,
                artifactSize = (long?)null,
                hasIntegrityMetadata = false,
                launchProfile = new
                {
                    kind = "PaperNogui",
                    arguments = "--nogui",
                    requiresEulaFile = true,
                    evidence = "PaperMC documents the headless --nogui launch form; ChunkPilot owns process lifecycle."
                },
                capabilities = new MinecraftVersionCapabilities
                {
                    ServerIcon = true,
                    FormattedMotd = true,
                    PlayerManagement = true,
                    ModernServerProperties = true,
                    StatusQuery = true,
                    Datapacks = true,
                    ManagedVersionChange = true
                },
                certification = option.Certification,
                warnings = Array.Empty<string>(),
                evidence = Array.Empty<string>(),
                provenance = option.Provenance
            })
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> CreationPaperBuildsAsync(JsonObject parameters)
    {
        var versionId = RequiredString(parameters, "versionId", 40);
        var forceRefresh = parameters["forceRefresh"]?.GetValue<bool?>() ?? false;
        var catalog = await paperCreation.GetBuildsAsync(versionId, forceRefresh).ConfigureAwait(true);
        paperBuildCatalogs[versionId] = catalog;
        return JsonSerializer.SerializeToNode(new
        {
            available = catalog.ProviderAvailable,
            message = catalog.UnavailableDetail,
            fromCache = catalog.IsFromCache,
            stale = catalog.IsStale,
            retrievedAt = catalog.RetrievedUtc,
            minecraftVersion = catalog.MinecraftVersion,
            builds = catalog.Builds.Select(build => new
            {
                id = build.BuildId,
                label = $"Build {build.BuildId}",
                channel = build.Channel.ToString(),
                publishedAt = build.PublishedAt,
                fileName = build.FileName,
                sizeBytes = build.ServerSizeBytes,
                hasIntegrityMetadata = build.HasIntegrityMetadata,
                selectable = build.IsSelectable,
                support = build.SupportTier.ToString(),
                certification = build.Certification,
                supportReason = build.SupportReason,
                provenance = build.Provenance
            })
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> LoaderCreationCatalogAsync(
        ManagedLoaderPlatform platform,
        bool forceRefresh)
    {
        var catalog = await loaderCreation.GetVersionsAsync(platform, forceRefresh).ConfigureAwait(true);
        loaderVersionCatalogs[platform] = catalog;
        return JsonSerializer.SerializeToNode(new
        {
            platform = platform.ToString(),
            available = catalog.ProviderAvailable,
            message = catalog.UnavailableDetail,
            fromCache = catalog.IsFromCache,
            stale = catalog.IsStale,
            retrievedAt = catalog.RetrievedUtc,
            manifestLatestReleaseId = "",
            manifestLatestSnapshotId = "",
            latestVerifiedReleaseId = catalog.Versions.FirstOrDefault(option =>
                option.SupportTier is MinecraftVersionSupportTier.Recommended or MinecraftVersionSupportTier.Verified)
                ?.MinecraftVersion ?? "",
            versions = catalog.Versions.Select(option => new
            {
                id = option.MinecraftVersion,
                label = option.MinecraftVersion,
                channel = option.StableMinecraft ? "Stable" : "Development",
                releaseKind = option.StableMinecraft ? "Release" : "Snapshot",
                releaseTime = (DateTimeOffset?)null,
                javaMajor = option.RequiredJavaMajor,
                javaSource = "ChunkPilotPolicy",
                support = option.IsSelectable ? option.SupportTier.ToString() : "Unavailable",
                supportReason = option.SupportReason,
                selectable = option.IsSelectable,
                hasServerArtifact = false,
                artifactSize = (long?)null,
                hasIntegrityMetadata = false,
                launchProfile = new
                {
                    kind = platform switch
                    {
                        ManagedLoaderPlatform.Fabric => "FabricServerLauncher",
                        ManagedLoaderPlatform.Quilt => "QuiltServerLauncher",
                        ManagedLoaderPlatform.Forge => "ForgeArgumentsFile",
                        ManagedLoaderPlatform.NeoForge => "NeoForgeArgumentsFile",
                        _ => "CatalogOnly"
                    },
                    arguments = "--nogui",
                    requiresEulaFile = true,
                    evidence = option.Provenance
                },
                capabilities = new MinecraftVersionCapabilities
                {
                    ServerIcon = true,
                    FormattedMotd = true,
                    PlayerManagement = true,
                    ModernServerProperties = true,
                    StatusQuery = true,
                    Datapacks = true,
                    ManagedVersionChange = true
                },
                certification = option.Certification,
                warnings = Array.Empty<string>(),
                evidence = Array.Empty<string>(),
                provenance = option.Provenance
            })
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> CreationLoaderBuildsAsync(JsonObject parameters)
    {
        var platformText = RequiredString(parameters, "platform", 20);
        if (!TryLoaderPlatform(platformText, out var platform))
            throw new ArgumentException("Select a supported managed-loader platform.");
        var versionId = RequiredString(parameters, "versionId", 64);
        var forceRefresh = parameters["forceRefresh"]?.GetValue<bool?>() ?? false;
        var catalog = await loaderCreation.GetBuildsAsync(platform, versionId, forceRefresh).ConfigureAwait(true);
        loaderBuildCatalogs[LoaderCatalogKey(platform, versionId)] = catalog;
        return JsonSerializer.SerializeToNode(new
        {
            platform = platform.ToString(),
            available = catalog.ProviderAvailable,
            message = catalog.UnavailableDetail,
            fromCache = catalog.IsFromCache,
            stale = catalog.IsStale,
            retrievedAt = catalog.RetrievedUtc,
            minecraftVersion = catalog.MinecraftVersion,
            builds = catalog.Builds.Select(build => new
            {
                id = build.LoaderVersion,
                label = platform switch
                {
                    ManagedLoaderPlatform.Fabric => $"Loader {build.LoaderVersion}",
                    ManagedLoaderPlatform.Quilt => $"Quilt Loader {build.LoaderVersion}",
                    ManagedLoaderPlatform.Forge => $"Forge {build.LoaderVersion}",
                    ManagedLoaderPlatform.NeoForge => $"NeoForge {build.LoaderVersion}",
                    ManagedLoaderPlatform.LegacyFabric => $"Legacy Fabric {build.LoaderVersion}",
                    ManagedLoaderPlatform.Ornithe => $"Ornithe {build.LoaderVersion}",
                    _ => build.LoaderVersion
                },
                loaderVersion = build.LoaderVersion,
                installerVersion = build.InstallerVersion,
                channel = build.Channel.ToString(),
                sizeBytes = build.ArtifactSizeBytes,
                hasIntegrityMetadata = build.HasProviderIntegrity,
                selectable = build.IsSelectable,
                support = build.SupportTier.ToString(),
                certification = build.Certification,
                supportReason = build.SupportReason,
                provenance = build.Provenance
            })
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> CreationDestinationAsync(JsonObject parameters)
    {
        var preview = await creation.PreviewDestinationAsync(
            RequiredString(parameters, "name", 80), OptionalString(parameters, "instanceRoot", 1024)).ConfigureAwait(true);
        return JsonSerializer.SerializeToNode(new
        {
            path = preview.CanonicalDestination,
            available = preview.IsAvailable,
            message = preview.Message
        }, WebUiProtocol.Json);
    }

    private static JsonNode? ChooseCreationFolder(JsonObject parameters)
    {
        var path = new DialogService().SelectFolder(
            "Choose where ChunkPilot should create managed servers",
            OptionalString(parameters, "startingPath", 1024));
        return JsonSerializer.SerializeToNode(new { path }, WebUiProtocol.Json);
    }

    private JsonNode? BeginCreationOperation(JsonObject parameters)
    {
        if (!Guid.TryParse(RequiredString(parameters, "operationId", 64), out var operationId))
            throw new ArgumentException("A valid client-generated creation operation ID is required.");
        if (creationOperations.ContainsKey(operationId))
            return PromptAcceptedOperation(operationId);

        var cancellation = new CancellationTokenSource();
        var operation = new CreationWebUiOperation(operationId, cancellation);
        creationOperations.Add(operationId, operation);
        operation.Registration = RegisterCreationAsync(
            (JsonObject)parameters.DeepClone(), operation, cancellation.Token);
        return PromptAcceptedOperation(operationId);
    }

    private async Task<Guid> RegisterCreationAsync(
        JsonObject parameters,
        CreationWebUiOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var registered = await BeginCreationCoreAsync(parameters, cancellationToken).ConfigureAwait(true);
            if (registered != operation.OperationId)
                throw new InvalidOperationException("The Agent registered creation under an unexpected operation identity.");
            return registered;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            operation.Error = exception is OperationCanceledException
                ? "Creation was cancelled before registration completed."
                : SecretRedactor.Redact(exception.Message);
            throw;
        }
    }

    private async Task<Guid> BeginCreationCoreAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var platformText = OptionalString(parameters, "platform", 20);
        if (platformText.Equals("Modpack", StringComparison.OrdinalIgnoreCase))
            return await BeginModpackCreationAsync(parameters, cancellationToken).ConfigureAwait(true);
        if (platformText.Equals("Paper", StringComparison.OrdinalIgnoreCase))
            return await BeginPaperCreationAsync(parameters, cancellationToken).ConfigureAwait(true);
        if (TryLoaderPlatform(platformText, out var loaderPlatform))
            return await BeginManagedLoaderCreationAsync(loaderPlatform, parameters, cancellationToken).ConfigureAwait(true);
        creationCatalog ??= await creation.GetCatalogAsync(true, false, cancellationToken).ConfigureAwait(true);
        var versionId = RequiredString(parameters, "versionId", 80);
        var legacyToken = OptionalString(parameters, "legacyArtifactToken", 128);
        var version = creationCatalog.Options.FirstOrDefault(option =>
            option.VersionId == versionId && (option.IsSelectable || !string.IsNullOrWhiteSpace(legacyToken)))
            ?? throw new ArgumentException("Select a supported Minecraft version from the authoritative catalog.");
        UserSuppliedServerArtifact? suppliedArtifact = null;
        if (!string.IsNullOrWhiteSpace(legacyToken))
        {
            if (version.HasServerDownload)
                throw new ArgumentException("The selected version uses its official Mojang server artifact.");
            suppliedArtifact = await legacyArtifactTokens.ConsumeAsync(
                version.VersionId, legacyToken, cancellationToken).ConfigureAwait(true);
        }
        if (version.SupportTier == MinecraftVersionSupportTier.Experimental &&
            parameters["experimentalAccepted"]?.GetValue<bool>() is not true)
        {
            throw new ArgumentException("You must acknowledge the experimental version warning before creation.");
        }
        if (parameters["eulaAccepted"]?.GetValue<bool>() is not true)
            throw new ArgumentException("You must deliberately accept the Minecraft EULA before creation.");
        var plan = new VanillaCreationPlan
        {
            OperationId = Guid.Parse(RequiredString(parameters, "operationId", 64)),
            ServerName = RequiredString(parameters, "name", 80),
            Version = version,
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            },
            MinimumRamMb = RequiredInt(parameters, "minimumRamMb", 512, 24 * 1024),
            MaximumRamMb = RequiredInt(parameters, "maximumRamMb", 1024, 24 * 1024),
            Port = RequiredInt(parameters, "port", 1, 65535),
            MaxPlayers = RequiredInt(parameters, "maxPlayers", 1, 1000, 10),
            InstanceRoot = OptionalString(parameters, "instanceRoot", 1024),
            NetworkingPreference = Enum.TryParse<VanillaNetworkingPreference>(OptionalString(parameters, "networking", 60), true, out var preference)
                ? preference : VanillaNetworkingPreference.DecideLater,
            MetadataRetrievedUtc = creationCatalog.RetrievedUtc,
            MetadataFromCache = creationCatalog.IsFromCache,
            UserSuppliedArtifact = suppliedArtifact,
            AcknowledgedWarnings = version.Warnings
        };
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems));
        return await creation.BeginAsync(plan, cancellationToken).ConfigureAwait(true);
    }

    private async Task<Guid> BeginModpackCreationAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        if (parameters["eulaAccepted"]?.GetValue<bool>() is not true)
            throw new ArgumentException("You must deliberately accept the Minecraft EULA before creation.");
        if (parameters["experimentalAccepted"]?.GetValue<bool>() is not true)
            throw new ArgumentException(
                "Acknowledge that this exact modpack release will be validated on demand during creation.");

        ModpackCreationPlan plan;
        var localToken = OptionalString(parameters, "localPackToken", 128);
        if (!string.IsNullOrWhiteSpace(localToken))
        {
            var selected = localImportTokens.Consume(localToken);
            var reviewed = await client.SendAsync<ServerImportInspection>("InspectServerImport",
                new ServerImportInspectRequest(selected.Path), cancellationToken).ConfigureAwait(true);
            if (!reviewed.CanInstall)
                throw new ArgumentException(reviewed.Limitation);
            if (reviewed.SourceKind != selected.Inspection.SourceKind ||
                reviewed.SourceSizeBytes != selected.Inspection.SourceSizeBytes ||
                !reviewed.Sha256.Equals(selected.Inspection.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected local server source changed after review. Choose it again.");
            if (reviewed.SourceKind != ServerImportSourceKind.ModrinthPack)
            {
                var management = Enum.TryParse<ServerImportManagementMode>(
                    OptionalString(parameters, "importManagementMode", 32), true, out var parsedManagement)
                    ? parsedManagement : ServerImportManagementMode.ManagedCopy;
                var launch = OptionalString(parameters, "importLaunchCandidate", 768);
                if (string.IsNullOrWhiteSpace(launch) && reviewed.LaunchCandidates.Count == 1)
                    launch = reviewed.LaunchCandidates[0];
                var importPlan = new ServerImportCreationPlan
                {
                    OperationId = Guid.Parse(RequiredString(parameters, "operationId", 64)),
                    NativePath = selected.Path,
                    Inspection = reviewed,
                    ManagementMode = management,
                    LaunchRelativePath = launch,
                    ServerName = RequiredString(parameters, "name", 80),
                    Eula = new VanillaEulaAcceptance
                    {
                        Accepted = true,
                        AcceptedAtUtc = DateTimeOffset.UtcNow,
                        SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
                    },
                    MinimumRamMb = RequiredInt(parameters, "minimumRamMb", 512, 24 * 1024),
                    MaximumRamMb = RequiredInt(parameters, "maximumRamMb", 1024, 24 * 1024),
                    Port = RequiredInt(parameters, "port", 1, 65535),
                    MaxPlayers = RequiredInt(parameters, "maxPlayers", 1, 1000, 10),
                    InstanceRoot = OptionalString(parameters, "instanceRoot", 1024),
                    NetworkingPreference = Enum.TryParse<VanillaNetworkingPreference>(
                        OptionalString(parameters, "networking", 60), true, out var importPreference)
                            ? importPreference : VanillaNetworkingPreference.DecideLater
                };
                var importProblems = importPlan.Problems();
                if (importProblems.Count > 0) throw new ArgumentException(string.Join(" ", importProblems));
                var importStarted = await client.SendAsync<InstallOperationRequest>("BeginServerImport",
                    new BeginServerImportRequest(importPlan), cancellationToken).ConfigureAwait(true);
                return importStarted.OperationId;
            }
            var inspection = await client.SendAsync<ModrinthPackInspection>("InspectModrinthPack",
                new ModrinthPackInspectRequest(selected.Path), cancellationToken).ConfigureAwait(true);
            if (!inspection.CanCreate) throw new ArgumentException(inspection.Limitation);
            plan = CommonPlan(new ModpackCreationPlan
            {
                SourceKind = ModpackCreationSource.LocalMrpack,
                Source = selected.Path,
                Provider = UpdateProvider.LocalPackageHistory,
                ProjectName = inspection.Name,
                VersionId = inspection.VersionName,
                VersionName = inspection.VersionName,
                MinecraftVersion = inspection.MinecraftVersion,
                RequiredJavaMajor = inspection.RequiredJavaMajor,
                ExpectedSha512 = inspection.ArchiveSha512,
                ExpectedSizeBytes = inspection.ArchiveSizeBytes
            });
        }
        else
        {
            if (!Enum.TryParse<CatalogProvider>(OptionalString(parameters, "modpackProvider", 32), true,
                    out var provider))
                provider = CatalogProvider.Modrinth;
            if (provider != CatalogProvider.Modrinth)
                throw new ArgumentException(
                    "This provider can be browsed, but ChunkPilot cannot yet create from its server-pack format. Choose a verified Modrinth release or import a reviewed local .mrpack.");
            var projectId = RequiredString(parameters, "modpackProjectId", 80);
            var versionId = RequiredString(parameters, "modpackVersionId", 80);
            if (!modpackCatalog.TryGetValue(CatalogKey(provider, projectId), out var project))
                throw new ArgumentException("Refresh the selected provider catalog before creating this pack.");
            var release = project.Versions.FirstOrDefault(version =>
                version.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase) &&
                version.HasServerPackage && version.Sha1.Length == 40 && version.Sha512.Length == 128 &&
                version.SizeBytes is > 0)
                ?? throw new ArgumentException("Select an exact integrity-verifiable Modrinth release.");
            plan = CommonPlan(new ModpackCreationPlan
            {
                SourceKind = ModpackCreationSource.Modrinth,
                Source = release.DownloadUrl,
                Provider = UpdateProvider.Modrinth,
                ProjectId = project.ProjectId,
                ProjectName = project.Name,
                VersionId = release.VersionId,
                VersionName = release.VersionName,
                ReleaseChannel = release.ReleaseChannel,
                MinecraftVersion = release.MinecraftVersion,
                RequiredJavaMajor = release.RequiredJavaMajor > 0
                    ? release.RequiredJavaMajor
                    : JavaRuntimePolicy.RequiredMajorForMinecraft(release.MinecraftVersion),
                ExpectedSha1 = release.Sha1,
                ExpectedSha512 = release.Sha512,
                ExpectedSizeBytes = release.SizeBytes
            });
        }

        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems));
        var started = await client.SendAsync<InstallOperationRequest>("BeginModpackCreation",
            new BeginModpackCreationRequest(plan), cancellationToken).ConfigureAwait(true);
        return started.OperationId;

        ModpackCreationPlan CommonPlan(ModpackCreationPlan source) => source with
        {
            OperationId = Guid.Parse(RequiredString(parameters, "operationId", 64)),
            ServerName = RequiredString(parameters, "name", 80),
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            },
            MinimumRamMb = RequiredInt(parameters, "minimumRamMb", 512, 24 * 1024),
            MaximumRamMb = RequiredInt(parameters, "maximumRamMb", 1024, 24 * 1024),
            Port = RequiredInt(parameters, "port", 1, 65535),
            MaxPlayers = RequiredInt(parameters, "maxPlayers", 1, 1000, 10),
            InstanceRoot = OptionalString(parameters, "instanceRoot", 1024),
            NetworkingPreference = Enum.TryParse<VanillaNetworkingPreference>(
                OptionalString(parameters, "networking", 60), true, out var preference)
                ? preference
                : VanillaNetworkingPreference.DecideLater,
            ExperimentalRuntimeRiskAccepted = true
        };
    }

    private async Task<Guid> BeginManagedLoaderCreationAsync(
        ManagedLoaderPlatform platform,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        if (!loaderVersionCatalogs.TryGetValue(platform, out var versions))
        {
            versions = await loaderCreation.GetVersionsAsync(platform, false, cancellationToken).ConfigureAwait(true);
            loaderVersionCatalogs[platform] = versions;
        }
        var versionId = RequiredString(parameters, "versionId", 64);
        var version = versions.Versions.FirstOrDefault(option => option.IsSelectable &&
            option.MinecraftVersion.Equals(versionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Select a supported Minecraft version from the official loader catalog.");
        var loaderVersion = RequiredString(parameters, "loaderVersion", 80);
        var key = LoaderCatalogKey(platform, versionId);
        if (!loaderBuildCatalogs.TryGetValue(key, out var builds))
        {
            builds = await loaderCreation.GetBuildsAsync(platform, versionId, false, cancellationToken).ConfigureAwait(true);
            loaderBuildCatalogs[key] = builds;
        }
        var build = builds.Builds.FirstOrDefault(option => option.IsSelectable &&
            option.LoaderVersion.Equals(loaderVersion, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Select an exact compatible loader version from the official catalog.");
        if (build.SupportTier == MinecraftVersionSupportTier.Experimental &&
            parameters["experimentalAccepted"]?.GetValue<bool>() is not true)
            throw new ArgumentException("Acknowledge that this exact loader combination is Experimental before creation.");
        if (parameters["eulaAccepted"]?.GetValue<bool>() is not true)
            throw new ArgumentException("You must deliberately accept the Minecraft EULA before creation.");
        var plan = new ManagedLoaderCreationPlan
        {
            OperationId = Guid.Parse(RequiredString(parameters, "operationId", 64)),
            ServerName = RequiredString(parameters, "name", 80),
            Version = version,
            Build = build,
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            },
            MinimumRamMb = RequiredInt(parameters, "minimumRamMb", 512, 24 * 1024),
            MaximumRamMb = RequiredInt(parameters, "maximumRamMb", 1024, 24 * 1024),
            Port = RequiredInt(parameters, "port", 1, 65535),
            MaxPlayers = RequiredInt(parameters, "maxPlayers", 1, 1000, 10),
            InstanceRoot = OptionalString(parameters, "instanceRoot", 1024),
            NetworkingPreference = Enum.TryParse<VanillaNetworkingPreference>(
                OptionalString(parameters, "networking", 60), true, out var preference)
                ? preference
                : VanillaNetworkingPreference.DecideLater,
            MetadataRetrievedUtc = builds.RetrievedUtc,
            MetadataFromCache = versions.IsFromCache || builds.IsFromCache,
            ExperimentalRuntimeRiskAccepted = build.SupportTier != MinecraftVersionSupportTier.Experimental ||
                                              parameters["experimentalAccepted"]?.GetValue<bool>() is true
        };
        var problems = plan.Problems();
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
        return await loaderCreation.BeginAsync(plan, cancellationToken).ConfigureAwait(true);
    }

    private static bool TryLoaderPlatform(string value, out ManagedLoaderPlatform platform) =>
        Enum.TryParse(value, true, out platform) &&
        platform is ManagedLoaderPlatform.Fabric or ManagedLoaderPlatform.Quilt or
            ManagedLoaderPlatform.Forge or ManagedLoaderPlatform.NeoForge or
            ManagedLoaderPlatform.LegacyFabric or ManagedLoaderPlatform.Ornithe;

    private static string LoaderCatalogKey(ManagedLoaderPlatform platform, string version) =>
        $"{platform}:{version}";

    private async Task<Guid> BeginPaperCreationAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        paperVersionCatalog ??= await paperCreation.GetVersionsAsync(false, cancellationToken).ConfigureAwait(true);
        var versionId = RequiredString(parameters, "versionId", 40);
        var version = paperVersionCatalog.Versions.FirstOrDefault(option =>
                          option.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase) && option.IsSelectable)
                      ?? throw new ArgumentException(
                          "Select a supported Paper Minecraft version from the authoritative catalog.");
        var buildId = RequiredInt(parameters, "buildId", 1, int.MaxValue);
        if (!paperBuildCatalogs.TryGetValue(version.VersionId, out var builds))
        {
            builds = await paperCreation.GetBuildsAsync(version.VersionId, false, cancellationToken).ConfigureAwait(true);
            paperBuildCatalogs[version.VersionId] = builds;
        }
        var build = builds.Builds.FirstOrDefault(option => option.BuildId == buildId && option.IsSelectable)
                    ?? throw new ArgumentException(
                        "Select an exact stable Paper build from the authoritative catalog.");
        if (build.SupportTier == MinecraftVersionSupportTier.Experimental &&
            parameters["experimentalAccepted"]?.GetValue<bool>() is not true)
        {
            throw new ArgumentException(
                "You must acknowledge that this Paper build has not been runtime-certified before creation.");
        }
        if (parameters["eulaAccepted"]?.GetValue<bool>() is not true)
            throw new ArgumentException("You must deliberately accept the Minecraft EULA before creation.");

        var plan = new PaperCreationPlan
        {
            OperationId = Guid.Parse(RequiredString(parameters, "operationId", 64)),
            ServerName = RequiredString(parameters, "name", 80),
            Version = version,
            Build = build,
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            },
            MinimumRamMb = RequiredInt(parameters, "minimumRamMb", 512, 24 * 1024),
            MaximumRamMb = RequiredInt(parameters, "maximumRamMb", 1024, 24 * 1024),
            Port = RequiredInt(parameters, "port", 1, 65535),
            MaxPlayers = RequiredInt(parameters, "maxPlayers", 1, 1000, 10),
            InstanceRoot = OptionalString(parameters, "instanceRoot", 1024),
            NetworkingPreference = Enum.TryParse<VanillaNetworkingPreference>(
                OptionalString(parameters, "networking", 60), true, out var preference)
                ? preference
                : VanillaNetworkingPreference.DecideLater,
            MetadataRetrievedUtc = builds.RetrievedUtc,
            MetadataFromCache = paperVersionCatalog.IsFromCache || builds.IsFromCache,
            ExperimentalRuntimeRiskAccepted = build.SupportTier != MinecraftVersionSupportTier.Experimental ||
                                                parameters["experimentalAccepted"]?.GetValue<bool>() is true
        };
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems));
        return await paperCreation.BeginAsync(plan, cancellationToken).ConfigureAwait(true);
    }

    private async Task<JsonNode?> CreationProgressAsync(JsonObject parameters)
    {
        if (!Guid.TryParse(RequiredString(parameters, "operationId", 64), out var operationId))
            throw new ArgumentException("A valid creation operation ID is required.");
        if (creationOperations.TryGetValue(operationId, out var submission))
        {
            var registration = submission.Registration;
            if (registration is null || !registration.IsCompleted)
            {
                return JsonSerializer.SerializeToNode(new
                {
                    operationId,
                    stage = "Preparing",
                    phase = "Validating",
                    percent = 1d,
                    message = "ChunkPilot accepted the request and is validating the exact provider selection.",
                    isTerminal = false,
                    success = (bool?)null,
                    error = (string?)null,
                    outcome = "Pending",
                    warnings = Array.Empty<string>()
                }, WebUiProtocol.Json);
            }
            if (registration.IsCanceled || registration.IsFaulted)
            {
                return JsonSerializer.SerializeToNode(new
                {
                    operationId,
                    stage = registration.IsCanceled ? "Cancelled" : "Failed",
                    phase = "Validation",
                    percent = 0d,
                    message = submission.Error ?? "ChunkPilot could not register the creation operation.",
                    isTerminal = true,
                    success = false,
                    error = submission.Error,
                    outcome = registration.IsCanceled ? "Cancelled" : "Failed",
                    warnings = Array.Empty<string>()
                }, WebUiProtocol.Json);
            }
        }
        var progress = await creation.GetSnapshotAsync(operationId).ConfigureAwait(true);
        return JsonSerializer.SerializeToNode(new
        {
            operationId = progress.OperationId,
            progress.Revision,
            progress.StartedAtUtc,
            progress.UpdatedAtUtc,
            stage = progress.Progress.Stage.ToString(),
            phase = progress.Progress.Phase.ToString(),
            percent = progress.Progress.OverallPercent,
            bytesDownloaded = progress.Progress.BytesDownloaded,
            totalBytes = progress.Progress.TotalBytes,
            bytesPerSecond = progress.Progress.BytesPerSecond,
            currentArtifact = progress.Progress.Detail,
            message = string.IsNullOrWhiteSpace(progress.Progress.CurrentStep)
                ? CreationStagePolicy.Describe(progress.Progress.Stage)
                : progress.Progress.CurrentStep,
            progress.IsTerminal,
            progress.Success,
            progress.Error,
            outcome = progress.Outcome.ToString(),
            progress.Warnings
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> CreationOperationsAsync()
    {
        var vanilla = client.SendAsync<VanillaCreationsResult>("VanillaCreations");
        var paper = client.SendAsync<PaperCreationsResult>("PaperCreations");
        var loaders = client.SendAsync<ManagedLoaderCreationsResult>("ManagedLoaderCreations");
        var modpacks = client.SendAsync<ModpackCreationsResult>("ModpackCreations");
        var imports = client.SendAsync<ServerImportOperationsResult>("ServerImportOperations");
        await Task.WhenAll(vanilla, paper, loaders, modpacks, imports).ConfigureAwait(true);
        var operations = vanilla.Result.Operations.Concat(paper.Result.Operations)
            .Concat(loaders.Result.Operations).Concat(modpacks.Result.Operations).Concat(imports.Result.Operations)
            .GroupBy(operation => operation.OperationId)
            .Select(group => group.OrderByDescending(operation => operation.Revision).First())
            .OrderByDescending(operation => operation.UpdatedAtUtc)
            .Select(progress => new
            {
                operationId = progress.OperationId,
                progress.Revision,
                progress.StartedAtUtc,
                progress.UpdatedAtUtc,
                stage = progress.Progress.Stage.ToString(),
                phase = progress.Progress.Phase.ToString(),
                percent = progress.Progress.OverallPercent,
                bytesDownloaded = progress.Progress.BytesDownloaded,
                totalBytes = progress.Progress.TotalBytes,
                bytesPerSecond = progress.Progress.BytesPerSecond,
                currentArtifact = progress.Progress.Detail,
                message = string.IsNullOrWhiteSpace(progress.Progress.CurrentStep)
                    ? CreationStagePolicy.Describe(progress.Progress.Stage)
                    : progress.Progress.CurrentStep,
                progress.IsTerminal,
                progress.Success,
                progress.Error,
                outcome = progress.Outcome.ToString(),
                progress.Warnings
            }).ToArray();
        return JsonSerializer.SerializeToNode(operations, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> CancelCreationAsync(JsonObject parameters)
    {
        if (!Guid.TryParse(RequiredString(parameters, "operationId", 64), out var operationId))
            throw new ArgumentException("A valid creation operation ID is required.");
        if (creationOperations.TryGetValue(operationId, out var submission) &&
            submission.Registration is { IsCompleted: false })
        {
            submission.Cancellation.Cancel();
            return JsonSerializer.SerializeToNode(new { accepted = true, operationId }, WebUiProtocol.Json);
        }
        await creation.CancelAsync(operationId).ConfigureAwait(true);
        return JsonSerializer.SerializeToNode(new { accepted = true, operationId }, WebUiProtocol.Json);
    }

    private void ApplyGlobalSettings(JsonObject parameters)
    {
        viewModel.MinimizeToTray = RequiredBool(parameters, "minimizeToTray");
        viewModel.StartMinimized = RequiredBool(parameters, "startMinimized");
        viewModel.StartWithWindows = RequiredBool(parameters, "startWithWindows");
        viewModel.ReducedMotion = RequiredBool(parameters, "reducedMotion");
    }

    private void ApplyServerSettings(JsonObject parameters)
    {
        Select(parameters);
        viewModel.PropertyMotd = RawString(parameters, "motd", 256);
        viewModel.PropertyPort = RequiredInt(parameters, "port", 1, 65535);
        viewModel.PropertyMaxPlayers = RequiredInt(parameters, "maximumPlayers", 1, 1000);
        viewModel.PropertyDifficulty = RequiredString(parameters, "difficulty", 20);
        viewModel.PropertyGameMode = RequiredString(parameters, "gameMode", 20);
        viewModel.PropertyPvp = RequiredBool(parameters, "pvp");
        viewModel.PropertyWhiteList = RequiredBool(parameters, "allowlist");
        viewModel.MinimumRamMb = RequiredInt(parameters, "minimumRamMb", 256, 24 * 1024);
        viewModel.MaximumRamMb = RequiredInt(parameters, "maximumRamMb", 512, 24 * 1024);
    }

    private async Task<JsonNode> ChooseAppearanceIconAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a server icon",
            Filter = ServerIconCropWindow.ImageFilter,
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return JsonSerializer.SerializeToNode(new { cancelled = true }, WebUiProtocol.Json)!;

        var file = new FileInfo(dialog.FileName);
        if (file.Length <= 0 || file.Length > 32L * 1024 * 1024)
            throw new InvalidDataException("Choose an image smaller than 32 MB.");

        await using var source = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var info = await ImageSharpImage.IdentifyAsync(source).ConfigureAwait(true)
            ?? throw new InvalidDataException("The selected file did not contain a readable image.");
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 40_000_000)
            throw new InvalidDataException("Choose an image with fewer than 40 million pixels.");
        source.Position = 0;
        using var image = await ImageSharpImage.LoadAsync<Rgba32>(source).ConfigureAwait(true);
        var sourceWidth = image.Width;
        var sourceHeight = image.Height;
        image.Mutate(context =>
        {
            context.AutoOrient();
            if (image.Width > 256 || image.Height > 256)
                context.Resize(new ResizeOptions
                {
                    Size = new ImageSharpSize(256, 256),
                    Mode = ImageSharpResizeMode.Max,
                    Sampler = KnownResamplers.Lanczos3
                });
        });
        await using var preview = new MemoryStream();
        await image.SaveAsync(preview, new PngEncoder()).ConfigureAwait(true);
        return JsonSerializer.SerializeToNode(new
        {
            cancelled = false,
            sourceUrl = $"data:image/png;base64,{Convert.ToBase64String(preview.ToArray())}",
            width = sourceWidth,
            height = sourceHeight,
            fileName = Path.GetFileName(dialog.FileName)
        }, WebUiProtocol.Json)!;
    }

    private async Task InstallAppearanceIconAsync(ServerSnapshot server, string base64)
    {
        var bytes = WebUiIconPayload.Decode64Png(base64);

        var dataRoot = Environment.GetEnvironmentVariable("CHUNKPILOT_DATA_ROOT");
        var root = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChunkPilot")
            : Path.GetFullPath(dataRoot);
        var staging = Path.Combine(root, "WebUi", "Staging");
        Directory.CreateDirectory(staging);
        var path = Path.Combine(staging, $"server-icon-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
            var result = await client.SendAsync<OperationResult>("InstallServerIcon",
                new IconInstallRequest(server.Definition.Id, path, SaveToLibrary: true)).ConfigureAwait(true);
            if (!result.Success)
                throw new InvalidOperationException(result.Message);
            snapshots.InvalidateServerIcon(server.Definition.Id);
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void SelectBackup(JsonObject parameters)
    {
        if (!Guid.TryParse(RequiredString(parameters, "backupId", 64), out var id))
            throw new ArgumentException("A valid backup ID is required.");
        viewModel.SelectedBackup = viewModel.Backups.FirstOrDefault(backup => backup.Id == id)
            ?? throw new ArgumentException("That backup is no longer available.");
    }

    private void SelectVersion(JsonObject parameters, bool requireRollbackReady)
    {
        if (!Guid.TryParse(RequiredString(parameters, "versionId", 64), out var id))
            throw new ArgumentException("A valid version ID is required.");
        var version = viewModel.Versions.FirstOrDefault(item => item.Id == id &&
            item.ServerId == viewModel.SelectedServer?.Definition.Id)
            ?? throw new ArgumentException("That version snapshot is no longer available.");
        if (requireRollbackReady && (version.IsActive || !version.Verified || !File.Exists(version.SnapshotPath)))
            throw new InvalidOperationException("That version is not a verified rollback target.");
        viewModel.SelectedVersion = version;
    }

    private async Task ModeratePlayerAsync(JsonObject parameters)
    {
        Select(parameters);
        var name = RequiredString(parameters, "playerName", 64);
        var row = viewModel.PlayerRows.FirstOrDefault(player =>
            string.Equals(player.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("That player is no longer in the authoritative player list.");
        switch (RequiredString(parameters, "action", 40))
        {
            case "AddToWhitelist": await row.WhitelistCommand.ExecuteAsync(null).ConfigureAwait(true); break;
            case "RemoveFromWhitelist": await row.UnwhitelistCommand.ExecuteAsync(null).ConfigureAwait(true); break;
            case "GrantOperator": await row.GrantOperatorCommand.ExecuteAsync(null).ConfigureAwait(true); break;
            case "RemoveOperator": await row.RemoveOperatorCommand.ExecuteAsync(null).ConfigureAwait(true); break;
            case "Kick": await row.KickCommand.ExecuteAsync(null).ConfigureAwait(true); break;
            case "Ban": await row.BanCommand.ExecuteAsync(null).ConfigureAwait(true); break;
            case "Pardon": await row.PardonCommand.ExecuteAsync(null).ConfigureAwait(true); break;
            default: throw new ArgumentException("That player moderation action is not allowed.");
        }
    }

    private ServerSnapshot RequireServer(JsonObject parameters)
    {
        Select(parameters);
        return viewModel.SelectedServer ?? throw new ArgumentException("Select a server first.");
    }

    private void Select(JsonObject parameters)
    {
        if (TryServer(parameters, out var server))
            viewModel.SelectServerCommand.Execute(server);
    }

    private bool TryServer(JsonObject parameters, out ServerSnapshot? server)
    {
        var text = OptionalString(parameters, "serverId", 64);
        server = Guid.TryParse(text, out var id)
            ? viewModel.Servers.FirstOrDefault(item => item.Definition.Id == id)
            : viewModel.SelectedServer;
        return server is not null;
    }

    private static JsonNode Accepted(string method) =>
        JsonSerializer.SerializeToNode(new { accepted = true, method, operationId = Guid.NewGuid() }, WebUiProtocol.Json)!;

    internal static JsonNode PromptAcceptedOperation(Guid operationId) =>
        JsonSerializer.SerializeToNode(new { accepted = true, operationId }, WebUiProtocol.Json)!;

    private static string RequiredString(JsonObject values, string name, int maximumLength)
    {
        var value = OptionalString(values, name, maximumLength);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.");
        return value;
    }

    private static string OptionalString(JsonObject values, string name, int maximumLength)
    {
        var value = values[name]?.GetValue<string>()?.Trim() ?? "";
        if (value.Length > maximumLength)
            throw new ArgumentException($"{name} is too long.");
        return value;
    }

    private static string RawString(JsonObject values, string name, int maximumLength)
    {
        var value = values[name]?.GetValue<string>()
            ?? throw new ArgumentException($"{name} is required.");
        if (value.Length > maximumLength)
            throw new ArgumentException($"{name} is too long.");
        return value;
    }

    private static int RequiredInt(JsonObject values, string name, int minimum, int maximum, int? fallback = null)
    {
        var value = values[name]?.GetValue<int?>() ?? fallback ?? throw new ArgumentException($"{name} is required.");
        if (value < minimum || value > maximum)
            throw new ArgumentException($"{name} must be from {minimum} to {maximum}.");
        return value;
    }

    private static bool RequiredBool(JsonObject values, string name) =>
        values[name]?.GetValue<bool?>() ?? throw new ArgumentException($"{name} is required.");

    private async void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        if (refreshInProgress || viewModel.IsBusy)
            return;
        refreshInProgress = true;
        try
        {
            var presentationVisible = IsVisible && WindowState != WindowState.Minimized;
            var now = DateTimeOffset.UtcNow;
            var activeServer = viewModel.Servers.Any(server => server.State is
                ServerState.Running or ServerState.Starting or ServerState.Stopping or ServerState.Restarting or
                ServerState.Saving or ServerState.BackingUp or ServerState.Restoring);
            var presentationInterval = activeServer
                ? ActivePresentationRefreshInterval
                : QuiescentPresentationRefreshInterval;
            var presentationDue = presentationVisible &&
                (lastPresentationRefreshAt == DateTimeOffset.MinValue || now - lastPresentationRefreshAt >= presentationInterval);
            if (presentationDue)
            {
                await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
                lastPresentationRefreshAt = now;
            }
            if (sessionId != Guid.Empty && now - lastHeartbeatAt >= TimeSpan.FromSeconds(2))
            {
                lastHeartbeatAt = now;
                _ = await client.SendAsync<OperationResult>("HeartbeatUiSession",
                    new UiSessionHeartbeatRequest(sessionId, RunningServerIds())
                    {
                        SessionCapability = sessionCapability
                    }).ConfigureAwait(true);
            }
            if (presentationDue && bridge is not null)
                await bridge.PublishSnapshotAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
        {
            viewModel.ShowRecoveryNotice($"The ChunkPilot Agent is unavailable: {exception.Message}");
        }
        finally
        {
            refreshInProgress = false;
        }
    }

    private void DragFromWebUi()
    {
        ReleaseCapture();
        _ = SendMessage(new System.Windows.Interop.WindowInteropHelper(this).Handle, WmNcLeftButtonDown, HtCaption, 0);
    }

    private void ShowFailure(string detail)
    {
        FailureDetail.Text = detail;
        FailureSurface.Visibility = Visibility.Visible;
    }

    private async void RetryButton_OnClick(object sender, RoutedEventArgs e) => await InitializeWebViewAsync().ConfigureAwait(true);

    private void RepairWebViewButton_OnClick(object sender, RoutedEventArgs e) =>
        OpenExternalHttps("https://developer.microsoft.com/microsoft-edge/webview2/");

    private void OpenDiagnosticsButton_OnClick(object sender, RoutedEventArgs e) =>
        viewModel.OpenLogsFolderCommand.Execute(null);

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        refreshTimer.Stop();
        if (Application.Current is not App application || application.IsUnexpectedExit || sessionId == Guid.Empty)
            return;
        client.TrySendOneWay("SafeApplicationExit",
            new SafeApplicationExitRequest(sessionId, RunningServerIds(), DateTimeOffset.UtcNow)
            {
                SessionCapability = sessionCapability
            });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        foreach (var operation in creationOperations.Values)
            operation.Cancellation.Dispose();
        creationOperations.Clear();
        localPluginTokens.Clear();
        localImportTokens.Clear();
        legacyArtifactTokens.Clear();
        modpackImageCache.Clear();
        modpackImages.Dispose();
        bridge?.Dispose();
        CoreWebView2? core = null;
        try
        {
            core = Browser.CoreWebView2;
        }
        catch (InvalidOperationException)
        {
            // The browser process can end immediately before the native window closes.
        }
        if (core is not null)
        {
            core.NavigationStarting -= CoreOnNavigationStarting;
            core.NewWindowRequested -= CoreOnNewWindowRequested;
            core.DownloadStarting -= CoreOnDownloadStarting;
            core.ProcessFailed -= CoreOnProcessFailed;
        }
        Browser.Dispose();
    }

    private IReadOnlyList<Guid> RunningServerIds() => viewModel.Servers
        .Where(server => server.State is not ServerState.Stopped and not ServerState.Crashed)
        .Select(server => server.Definition.Id)
        .ToArray();

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int message, int wParam, int lParam);
}
