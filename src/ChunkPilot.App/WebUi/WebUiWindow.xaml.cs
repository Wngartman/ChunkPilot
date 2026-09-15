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
        method is not "workspace.load" and not "players.head" and not "help.openExternal" and
            not "modpacks.invalidatePreflight" and not "content.invalidatePlan" &&
        !method.StartsWith("connectivity.", StringComparison.Ordinal);
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
    private readonly CurseForgePreflightEvidenceStore curseForgePreflightEvidence = new();
    private readonly CurseForgeManagedContentPlanEvidenceStore curseForgeContentPlanEvidence = new();
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
    private readonly WebUiWorldSourceTokenStore worldSourceTokens = new();
    private readonly CreationWorldSourceService creationWorldSources = new();
    private readonly ModpackImageLoader modpackImages;
    private readonly PlayerHeadImageService playerHeads = new();
    private readonly WebUiIconEditStore iconEdits = new();

    public WebUiWindow(MainViewModel viewModel, AgentClient client)
    {
        InitializeComponent();
        Browser.DefaultBackgroundColor = WebUiNativeTheme.ResolveWebViewColor("AppSurfaceCanvas");
        this.viewModel = viewModel;
        this.client = client;
        creation = new AgentVanillaCreationGateway(client);
        paperCreation = new AgentPaperCreationGateway(client);
        loaderCreation = new AgentManagedLoaderCreationGateway(client);
        var modpackImageClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate
        }, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        modpackImageClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ChunkPilot/1.3.0 (local Windows Minecraft server manager)");
        modpackImages = new ModpackImageLoader(modpackImageClient, disposeClient: true);
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
        core.Settings.AreDefaultScriptDialogsEnabled = false;
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

    private static void OpenHelpSource(string value)
    {
        var uri = RequireAllowedHelpSource(value);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    internal static Uri RequireAllowedHelpSource(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new ArgumentException("Help sources must use HTTPS.");
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "www.minecraft.net", "minecraft.net", "help.minecraft.net",
            "docs.papermc.io", "docs.fabricmc.net", "docs.neoforged.net", "docs.minecraftforge.net",
            "support.modrinth.com", "learn.microsoft.com", "docs.oracle.com", "minecraft.wiki", "starlink.com"
        };
        if (!allowedHosts.Contains(uri.Host))
            throw new ArgumentException("That help source host is not allowed.");
        return uri;
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
        CancellationToken cancellationToken)
    {
        if (IsCancellableAddonRequestMethod(method))
            return DispatchAddonRequestAsync(method, parameters, cancellationToken);

        return method switch
        {
            "modpacks.versions" => LoadModpackVersionsAsync(parameters, cancellationToken),
            "modpacks.cache" => SearchModpacksAsync(parameters, cacheOnly: true, cancellationToken),
            "modpacks.search" => SearchModpacksAsync(parameters, cacheOnly: false, cancellationToken),
            "modpacks.project" => ResolveModpackProjectAsync(parameters, cancellationToken),
            "modpacks.resolveLink" => ResolveModpackLinkAsync(parameters, cancellationToken),
            "modpacks.preflight" => PreflightCurseForgeModpackAsync(parameters, cancellationToken),
            "modpacks.image" => LoadModpackImageAsync(parameters, cancellationToken),
            "versions.check" => CheckForServerUpdatesAsync(parameters, cancellationToken),
            "versions.markHealthy" => MarkVersionHealthyFromWebUiAsync(parameters, cancellationToken),
            "versions.rollback" => RollbackVersionFromWebUiAsync(parameters, cancellationToken),
            _ => DispatchAsync(method, parameters)
        };
    }

    internal static bool IsCancellableAddonRequestMethod(string method) => method is
        "plugins.providers" or "mods.providers" or
        "plugins.search" or "mods.search" or
        "plugins.release" or "mods.release" or
        "plugins.plan" or "mods.plan" or
        "plugins.install" or "mods.install" or
        "plugins.installPlan" or "mods.installPlan" or
        "content.operations" or "content.invalidatePlan";

    private async Task<JsonNode?> DispatchAddonRequestAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        if (method == "content.invalidatePlan")
        {
            if (!Guid.TryParse(RequiredString(parameters, "authorizationId", 64), out var authorizationId) ||
                authorizationId == Guid.Empty)
                throw new ArgumentException("A valid dependency-plan authorization ID is required.");
            curseForgeContentPlanEvidence.InvalidateIfAuthorizationId(authorizationId);
            await RevokeCurseForgeContentPlanAsync(authorizationId).ConfigureAwait(true);
            return Accepted(method);
        }

        var deferCancellationForExactCleanup = method is
            "plugins.plan" or "mods.plan" or "plugins.installPlan" or "mods.installPlan";
        var serverId = RequireCurrentAddonServer(
            parameters,
            deferCancellationForExactCleanup ? CancellationToken.None : cancellationToken);
        switch (method)
        {
            case "plugins.providers":
            case "mods.providers":
            {
                var statuses = await ExecuteFencedAddonMetadataRequestAsync(
                    serverId,
                    () => viewModel.SelectedServer?.Definition.Id,
                    token => client.SendAsync<IReadOnlyList<PluginProviderStatus>>(
                        "PluginProviders", new ServerIdRequest(serverId), token),
                    cancellationToken).ConfigureAwait(true);
                return JsonSerializer.SerializeToNode(statuses, WebUiProtocol.Json);
            }
            case "plugins.search":
            case "mods.search":
            {
                var results = await ExecuteFencedAddonMetadataRequestAsync(
                    serverId,
                    () => viewModel.SelectedServer?.Definition.Id,
                    token => client.SendAsync<IReadOnlyList<PluginProject>>(
                        "PluginSearch",
                        new PluginSearchRequest(
                            serverId,
                            parameters["search"]?.GetValue<string>()?.Trim() ?? "",
                            RequiredInt(parameters, "limit", 1, 40, 20),
                            ParseAddonProvider(parameters)),
                        token),
                    cancellationToken).ConfigureAwait(true);
                // Provider image URLs are intentionally not sent to the renderer. Production CSP
                // permits only app-local images; a future native image cache can add them safely.
                return SerializeAddonProjects(results);
            }
            case "plugins.release":
            case "mods.release":
            {
                var release = await ExecuteFencedAddonMetadataRequestAsync(
                    serverId,
                    () => viewModel.SelectedServer?.Definition.Id,
                    token => client.SendAsync<PluginRelease?>(
                        "PluginRelease",
                        new PluginReleaseRequest(
                            serverId,
                            RequiredString(parameters, "projectId", 80),
                            ParseAddonProvider(parameters)),
                        token),
                    cancellationToken).ConfigureAwait(true);
                return SerializeAddonRelease(release);
            }
            case "plugins.plan":
            case "mods.plan":
            {
                var projectId = RequiredString(parameters, "projectId", 80);
                var versionId = RequiredString(parameters, "versionId", 80);
                var provider = ParseAddonProvider(parameters);
                var invalidation = curseForgeContentPlanEvidence.InvalidateWithEvidence();
                await RevokeCurseForgeContentPlanAsync(invalidation.AuthorizationId).ConfigureAwait(true);
                Guid? provisionalAuthorization = null;
                try
                {
                    var plan = await ExecuteFencedAddonMetadataRequestAsync(
                        serverId,
                        () => viewModel.SelectedServer?.Definition.Id,
                        async token =>
                        {
                            var result = await client.SendAsync<PluginInstallPlan>(
                                "PlanPluginProviderRelease",
                                new PluginProviderPlanRequest(
                                    serverId, projectId, versionId, provider),
                                token).ConfigureAwait(true);
                            provisionalAuthorization = result.Authorization?.AuthorizationId;
                            return result;
                        },
                        cancellationToken).ConfigureAwait(true);
                    if (provider == PluginProviderKind.CurseForge && plan.CanInstall)
                    {
                        var authorization = plan.Authorization ?? throw new InvalidDataException(
                            "The reviewed CurseForge dependency plan lacks its exact Agent authorization.");
                        if (!curseForgeContentPlanEvidence.TryCommit(
                                invalidation.Generation, serverId, provider, projectId, versionId,
                                authorization, out _))
                            throw new InvalidOperationException(
                                "The selected add-on release changed while its dependency plan was loading. Review it again.");
                    }
                    else
                    {
                        if (plan.Authorization is not null)
                            throw new InvalidDataException(
                                "Only an installable CurseForge dependency plan may carry Agent authorization.");
                        if (!curseForgeContentPlanEvidence.IsCurrent(invalidation.Generation))
                            throw new InvalidOperationException(
                                "The selected add-on release changed while its dependency plan was loading. Review it again.");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    var response = JsonSerializer.SerializeToNode(plan, WebUiProtocol.Json);
                    provisionalAuthorization = null;
                    return response;
                }
                finally
                {
                    if (provisionalAuthorization is { } authorizationId)
                    {
                        curseForgeContentPlanEvidence.InvalidateIfAuthorizationId(authorizationId);
                        await RevokeCurseForgeContentPlanAsync(authorizationId).ConfigureAwait(true);
                    }
                }
            }
            case "plugins.install":
            case "mods.install":
            case "plugins.installPlan":
            case "mods.installPlan":
            {
                var includeDependencies = method.EndsWith("installPlan", StringComparison.Ordinal);
                Guid? authorizationToRevoke = TryReadManagedContentPlanAuthorizationId(parameters);
                try
                {
                    var projectId = RequiredString(parameters, "projectId", 80);
                    var versionId = RequiredString(parameters, "versionId", 80);
                    var provider = ParseAddonProvider(parameters);
                    ManagedContentPlanAuthorization? planAuthorization;
                    try
                    {
                        planAuthorization = ParseManagedContentPlanAuthorization(parameters);
                    }
                    catch (ArgumentException)
                    {
                        authorizationToRevoke = curseForgeContentPlanEvidence.InvalidateIfSelection(
                            serverId, provider, projectId, versionId);
                        throw;
                    }

                    if (includeDependencies && provider == PluginProviderKind.CurseForge)
                    {
                        if (planAuthorization is null)
                        {
                            authorizationToRevoke = curseForgeContentPlanEvidence.InvalidateIfSelection(
                                serverId, provider, projectId, versionId);
                            throw new InvalidOperationException(
                                "CurseForge dependency installation requires the exact reviewed plan authorization.");
                        }
                        authorizationToRevoke = planAuthorization.AuthorizationId;
                        _ = curseForgeContentPlanEvidence.Consume(
                            serverId, provider, projectId, versionId, planAuthorization);
                    }
                    else if (planAuthorization is not null)
                    {
                        authorizationToRevoke = planAuthorization.AuthorizationId;
                        curseForgeContentPlanEvidence.InvalidateIfAuthorizationId(
                            planAuthorization.AuthorizationId);
                        throw new ArgumentException(
                            "A dependency-plan authorization is valid only for a CurseForge dependency installation.");
                    }

                    var operationId = RequireClientOperationId(
                        parameters, "managed-content");
                    var request = new BeginManagedContentInstallRequest(
                        serverId,
                        projectId,
                        versionId,
                        includeDependencies,
                        parameters["restartIfRunning"]?.GetValue<bool?>() ?? false,
                        operationId,
                        provider,
                        planAuthorization);
                    var result = await ExecuteFencedAddonInstallRequestAsync(
                        serverId,
                        () => viewModel.SelectedServer?.Definition.Id,
                        async token =>
                        {
                            var accepted = await AgentOperationAcceptance.BeginManagedContentInstallAsync(
                                request,
                                beginToken => client.SendAsync<ManagedContentOperationSnapshot>(
                                    "BeginManagedContentInstall", request, beginToken),
                                fenceToken => client.SendAsync<ManagedContentCancellationFenceResult>(
                                    "CancelOrFenceManagedContentOperation",
                                    request,
                                    fenceToken),
                                token).ConfigureAwait(true);
                            // A successful Agent response means the exact one-time plan was consumed.
                            authorizationToRevoke = null;
                            return accepted;
                        },
                        EnsureContentOperationObserver,
                        cancellationToken).ConfigureAwait(true);
                    return JsonSerializer.SerializeToNode(result, WebUiProtocol.Json);
                }
                finally
                {
                    if (authorizationToRevoke is { } authorizationId)
                    {
                        curseForgeContentPlanEvidence.InvalidateIfAuthorizationId(authorizationId);
                        await RevokeCurseForgeContentPlanAsync(authorizationId).ConfigureAwait(true);
                    }
                }
            }
            case "content.operations":
            {
                var operations = await ExecuteFencedAddonMetadataRequestAsync(
                    serverId,
                    () => viewModel.SelectedServer?.Definition.Id,
                    token => client.SendAsync<IReadOnlyList<ManagedContentOperationSnapshot>>(
                        "ManagedContentOperations",
                        new ManagedContentOperationsRequest(serverId),
                        token),
                    cancellationToken).ConfigureAwait(true);
                foreach (var operation in operations.Where(operation => !operation.IsTerminal))
                    EnsureContentOperationObserver(operation);
                return JsonSerializer.SerializeToNode(operations, WebUiProtocol.Json);
            }
            default:
                throw new ArgumentException($"The add-on bridge method '{method}' is not supported.");
        }
    }

    private Guid RequireCurrentAddonServer(JsonObject parameters, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(RequiredString(parameters, "serverId", 64), out var serverId))
            throw new ArgumentException("A valid server ID is required.");
        RequireSelectedAddonServer(serverId, viewModel.SelectedServer?.Definition.Id, cancellationToken);
        return serverId;
    }

    internal static void RequireSelectedAddonServer(
        Guid requestedServerId,
        Guid? selectedServerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedServerId != requestedServerId)
            throw new InvalidOperationException(
                "The selected server changed before the add-on request could complete. No other server was selected or modified.");
    }

    internal static async Task<T> ExecuteFencedAddonMetadataRequestAsync<T>(
        Guid serverId,
        Func<Guid?> selectedServerId,
        Func<CancellationToken, Task<T>> request,
        CancellationToken cancellationToken)
    {
        RequireSelectedAddonServer(serverId, selectedServerId(), cancellationToken);
        var result = await request(cancellationToken).ConfigureAwait(true);
        RequireSelectedAddonServer(serverId, selectedServerId(), cancellationToken);
        return result;
    }

    internal static async Task<T> ExecuteFencedAddonInstallRequestAsync<T>(
        Guid serverId,
        Func<Guid?> selectedServerId,
        Func<CancellationToken, Task<T>> request,
        Action<T> observeStartedOperation,
        CancellationToken cancellationToken)
    {
        RequireSelectedAddonServer(serverId, selectedServerId(), cancellationToken);
        var result = await request(cancellationToken).ConfigureAwait(true);
        observeStartedOperation(result);
        RequireSelectedAddonServer(serverId, selectedServerId(), cancellationToken);
        return result;
    }

    private static JsonNode? SerializeAddonProjects(IReadOnlyList<PluginProject> results) =>
        JsonSerializer.SerializeToNode(results.Select(project => new
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

    private static JsonNode? SerializeAddonRelease(PluginRelease? release) =>
        JsonSerializer.SerializeToNode(release is null ? null : new
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
            integrity = release.Sha512.Length == 128 ? "sha512" :
                release.Provider == PluginProviderKind.CurseForge && release.Sha1.Length == 40
                    ? "sha1" : "unavailable",
            serverSide = release.ServerSide,
            clientSide = release.ClientSide,
            clientRequirement = release.ClientRequirement,
            kind = release.Kind.ToString(),
            dependencies = release.Dependencies
        }, WebUiProtocol.Json);

    private async Task<JsonNode?> DispatchAsync(string method, JsonObject parameters)
    {
        switch (method)
        {
            case "snapshot.refresh":
                await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
                return snapshots.Capture(viewModel);
            case "snapshot.selectServer":
                if (TryServer(parameters, out var selected))
                {
                    await InvalidateCurseForgeContentPlanForServerChangeAsync(
                        selected?.Definition.Id).ConfigureAwait(true);
                    viewModel.SelectServerCommand.Execute(selected);
                }
                else
                {
                    await InvalidateCurseForgeContentPlanForServerChangeAsync(null).ConfigureAwait(true);
                    viewModel.NavigateCommand.Execute("Servers");
                }
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
            case "help.openExternal":
                OpenHelpSource(RequiredString(parameters, "url", 512));
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
                return await ChooseAppearanceIconAsync(RequireServer(parameters).Definition).ConfigureAwait(true);
            case "appearance.editIcon":
                return JsonSerializer.SerializeToNode(iconEdits.OpenExisting(RequireServer(parameters).Definition), WebUiProtocol.Json);
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
            case "modpacks.project":
                return await ResolveModpackProjectAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            case "modpacks.resolveLink":
                return await ResolveModpackLinkAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            case "modpacks.preflight":
                return await PreflightCurseForgeModpackAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            case "modpacks.image":
                return await LoadModpackImageAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            case "modpacks.invalidatePreflight":
                await InvalidateCurseForgePreflightAsync().ConfigureAwait(true);
                return Accepted(method);
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
                    throw new InvalidOperationException("Start the server before changing the whitelist.");
                await viewModel.AddWhitelistPlayerCommand.ExecuteAsync(null).ConfigureAwait(true);
                if (viewModel.HasAccessError)
                    throw new InvalidOperationException(viewModel.AccessErrorMessage);
                break;
            case "players.head":
            {
                var requestedServerId = Guid.Parse(RequiredString(parameters, "serverId", 64));
                if (viewModel.SelectedServer?.Definition.Id != requestedServerId)
                    throw new InvalidOperationException("Player identity is no longer current for the selected server.");
                var uuid = Guid.Parse(RequiredString(parameters, "uuid", 64));
                if (!viewModel.PlayerRows.Any(row => row.Uuid == uuid))
                    throw new ArgumentException("That authoritative player UUID is not present for this server.");
                var imageUrl = await playerHeads.GetDataUrlAsync(uuid).ConfigureAwait(true);
                return JsonSerializer.SerializeToNode(new { serverId = requestedServerId, uuid, imageUrl }, WebUiProtocol.Json)!;
            }
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
                var settingsServerId = Guid.Parse(RequiredString(parameters, "serverId", 64));
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
                    if (viewModel.SelectedServer?.Definition.Id != settingsServerId)
                        throw new InvalidOperationException("The selected server changed before memory settings could be saved. The new server was not modified.");
                    await viewModel.ApplyMemoryCommand.ExecuteAsync(null).ConfigureAwait(true);
                    if (viewModel.MemorySaveError.Length > 0 || viewModel.HasMemoryChanges)
                        throw new InvalidOperationException(viewModel.MemorySaveError.Length > 0
                            ? viewModel.MemorySaveError
                            : "The memory allocation was not confirmed by the authoritative settings service.");
                }
                var iconBase64 = parameters["iconPngBase64"]?.GetValue<string>();
                var iconEdit = parameters["iconEdit"] as JsonObject;
                if (!string.IsNullOrWhiteSpace(iconBase64) || iconEdit is not null)
                {
                    try
                    {
                        var iconServer = RequireServer(parameters);
                        WebUiPreparedIcon? prepared = null;
                        if (iconEdit is not null)
                        {
                            var recipe = iconEdit["recipe"]?.Deserialize<WebUiIconRecipe>(WebUiProtocol.Json)
                                ?? throw new InvalidDataException("An icon edit recipe is required.");
                            prepared = iconEdits.Prepare(iconServer.Definition, RequiredString(iconEdit, "token", 64), recipe);
                            iconBase64 = Convert.ToBase64String(prepared.Output);
                        }
                        await InstallAppearanceIconAsync(iconServer, iconBase64!, prepared?.ExpectedIconHash).ConfigureAwait(true);
                        if (prepared is not null)
                        {
                            try { await iconEdits.SaveRecipeAsync(iconServer.Definition, prepared).ConfigureAwait(true); }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                            {
                                throw new IOException("The icon was saved, but its reusable source could not be recorded. Reopen the editor before editing again.", exception);
                            }
                        }
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
                return await CheckForServerUpdatesAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            case "versions.install":
                Select(parameters);
                if (viewModel.CurrentUpdateCheck?.LatestVersion is null)
                    throw new InvalidOperationException("No installable update has been confirmed for this server.");
                return await BeginUpdateOperationAsync(parameters).ConfigureAwait(true);
            case "versions.markHealthy":
                return await MarkVersionHealthyFromWebUiAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            case "versions.rollback":
                return await RollbackVersionFromWebUiAsync(parameters, CancellationToken.None).ConfigureAwait(true);
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
                var connection = WebUiSnapshotMapper.ConnectionSummary(viewModel, addressServer);
                var kind = RequiredString(parameters, "kind", 20).ToLowerInvariant();
                var address = kind switch
                {
                    "local" when connection.LocalAddress is not null => connection.LocalAddress,
                    "local" when addressServer.State == ServerState.Stopped && connection.ConfiguredLocalAddress is not null => connection.ConfiguredLocalAddress,
                    "lan" when connection.LanAddress is not null => connection.LanAddress,
                    "lan" when addressServer.State == ServerState.Stopped && connection.ConfiguredLanAddress is not null => connection.ConfiguredLanAddress,
                    "public" when connection.PublicVerifiedAddress is not null => connection.PublicVerifiedAddress,
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
                    "local" => throw new InvalidOperationException("ChunkPilot has not established a local address for this server."),
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
                    networkMode is not (NetworkMode.ThisComputerOnly or NetworkMode.HomeNetwork or NetworkMode.PortForwarding))
                    throw new ArgumentException("Choose Local only, LAN, or Internet hosting.");
                viewModel.SelectedNetworkMode = networkMode;
                await viewModel.SaveNetworkModeCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "connectivity.applyBinding":
            {
                var target = RequireServer(parameters);
                if (!Enum.TryParse<NetworkMode>(RequiredString(parameters, "mode", 40), true, out var bindingMode))
                    throw new ArgumentException("Choose a supported connection preference.");
                var result = await client.SendAsync<OperationResult>("ApplyServerBinding",
                    new ApplyServerBindingRequest(target.Definition.Id, bindingMode,
                        RequiredBool(parameters, "confirmed"), RequiredBool(parameters, "restartIfRunning"))
                    {
                        Session = new() { SessionId = sessionId, Capability = sessionCapability }
                    }).ConfigureAwait(true);
                await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
                await bridge!.PublishSnapshotAsync().ConfigureAwait(true);
                return JsonSerializer.SerializeToNode(result, WebUiProtocol.Json);
            }
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
            case "creation.chooseWorld":
                return await ChooseCreationWorldAsync(parameters).ConfigureAwait(true);
            case "creation.begin":
                return BeginCreationOperation(parameters);
            case "creation.operations":
                return await CreationOperationsAsync().ConfigureAwait(true);
            case "creation.progress":
                return await CreationProgressAsync(parameters).ConfigureAwait(true);
            case "creation.cancel":
                return await CancelCreationAsync(parameters).ConfigureAwait(true);
            case "creation.retry":
                return await RecoverCreationAsync(parameters, retry: true).ConfigureAwait(true);
            case "creation.discard":
                return await RecoverCreationAsync(parameters, retry: false).ConfigureAwait(true);
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

        var migrationResolutions = ValidateMigrationReviewParameters(
            parameters,
            server.Definition.Id,
            target,
            viewModel.CurrentUpdateOperation);
        var requestedOperationId = parameters["operationId"]?.GetValue<Guid?>() ?? Guid.NewGuid();
        var started = await client.SendAsync<UpdateOperationRequest>("BeginPackUpdate", new UpdateInstallRequest
        {
            OperationId = requestedOperationId,
            ServerId = server.Definition.Id,
            TargetVersion = target,
            ReviewedOperationId = migrationResolutions.Count > 0
                ? Guid.Parse(RequiredString(parameters, "reviewedOperationId", 64))
                : null,
            PlayerCountdownSeconds = server.State == ServerState.Running ? 30 : 0,
            StartForValidation = true,
            ConfirmedMigrationWarnings = migrationResolutions.Count > 0,
            MigrationResolutions = migrationResolutions
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

    private async Task<JsonNode?> CheckForServerUpdatesAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var serverId = RequireServer(parameters).Definition.Id;
        await viewModel.CheckForUpdatesForServerAsync(serverId, cancellationToken).ConfigureAwait(true);
        return null;
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
            // Discovery is intentionally summary-only. Exact server-path and distribution evidence is
            // resolved after the user selects a project; unknown is not the same as unsupported.
            ServerPackRequired = false,
            ExcludeClientOnly = false,
            Limit = RequiredInt(parameters, "limit", 1, 50, 50),
            Index = RequiredInt(parameters, "index", 0, 10_000, 0),
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
            result.Stale,
            result.NextIndex,
            result.HasMore,
            result.TotalCount
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> ResolveModpackProjectAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CatalogProvider>(RequiredString(parameters, "provider", 32), true,
                out var provider) || provider is not (CatalogProvider.Modrinth or CatalogProvider.CurseForge))
            throw new ArgumentException("The modpack provider is invalid.");
        var projectId = RequiredString(parameters, "projectId", 80);
        var item = await client.SendAsync<CatalogItem?>("ResolveCatalogProject",
            new CatalogProjectRequest(provider, projectId), cancellationToken).ConfigureAwait(true);
        if (item is null || item.Provider != provider ||
            !item.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The provider project could not be resolved.");
        modpackCatalog[CatalogKey(item.Provider, item.ProjectId)] = item;
        return JsonSerializer.SerializeToNode(ToWebModpackProject(item), WebUiProtocol.Json);
    }

    private async Task<JsonNode?> ResolveModpackLinkAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var reference = ProviderLinkParser.Parse(RequiredString(parameters, "url", 2048));
        if (reference.ContentType != CatalogContentType.Modpack)
            throw new InvalidOperationException("That is a CurseForge mod link. Open this server's Mods page to review exact compatible files.");
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
        var item = await client.SendAsync<CatalogItem?>("ResolveCatalogProject",
            new CatalogProjectRequest(reference.Provider, reference.ProjectReference,
                reference.ReleaseReference), cancellationToken).ConfigureAwait(true);
        if (item is null)
            throw new InvalidOperationException("The provider project could not be resolved from that link.");
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

    internal static IReadOnlyDictionary<string, MigrationResolution> ValidateMigrationReviewParameters(
        JsonObject parameters,
        Guid selectedServerId,
        PackVersionInfo authoritativeTarget,
        UpdateOperationSnapshot? currentOperation)
    {
        var requestedTarget = RequiredString(parameters, "targetVersionId", 160);
        if (!requestedTarget.Equals(authoritativeTarget.VersionId, StringComparison.Ordinal))
            throw new InvalidOperationException("The reviewed update target is stale. Check for updates again.");

        var confirmed = parameters["confirmedMigrationWarnings"]?.GetValue<bool?>() ?? false;
        if (!confirmed)
        {
            if (parameters.ContainsKey("reviewedOperationId") || parameters.ContainsKey("migrationResolutions"))
                throw new ArgumentException("Migration review fields require explicit confirmation.");
            return new Dictionary<string, MigrationResolution>(StringComparer.OrdinalIgnoreCase);
        }

        if (!Guid.TryParse(RequiredString(parameters, "reviewedOperationId", 64), out var reviewedOperationId))
            throw new ArgumentException("reviewedOperationId is invalid.");
        if (currentOperation is not
            {
                IsTerminal: true,
                Success: false,
                Progress.State: UpdateOperationState.PlanningMigration,
                Result: { } result
            } ||
            currentOperation.OperationId != reviewedOperationId ||
            result.OperationId != reviewedOperationId ||
            result.ServerId != selectedServerId ||
            !result.TargetVersionId.Equals(authoritativeTarget.VersionId, StringComparison.Ordinal) ||
            !result.MigrationPlan.RequiresManualReview)
            throw new InvalidOperationException("The migration review is stale or belongs to another server or target.");

        var conflictPaths = WebUiSnapshotMapper.MigrationConflictPaths(result.MigrationPlan);
        var conflictSet = conflictPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (conflictPaths.Count == 0 ||
            conflictPaths.Count != result.MigrationPlan.Conflicts.Count ||
            conflictPaths.Count != conflictSet.Count ||
            conflictPaths.Count > WebUiSnapshotMapper.MaximumMigrationConflicts ||
            conflictPaths.Any(path => path.Length > 1024 || !result.MigrationPlan.Changes.Any(change =>
                change.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("The migration review cannot be resolved safely in the WebUI.");

        if (parameters["migrationResolutions"] is not JsonObject supplied || supplied.Count != conflictPaths.Count)
            throw new ArgumentException("Choose a resolution for every migration conflict.");
        var resolutions = new Dictionary<string, MigrationResolution>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in supplied)
        {
            if (!conflictSet.Contains(pair.Key) || !resolutions.TryAdd(pair.Key, new MigrationResolution()))
                throw new ArgumentException("Migration resolutions contain an unexpected or duplicate path.");
            var choice = pair.Value?.GetValue<string>() ?? "";
            var kind = choice switch
            {
                "KeepOld" => MigrationResolutionKind.KeepOld,
                "NewBaseline" => MigrationResolutionKind.NewBaseline,
                _ => throw new ArgumentException(
                    "Each migration conflict must use KeepOld or NewBaseline.")
            };
            resolutions[pair.Key] = new MigrationResolution { Kind = kind };
        }
        if (conflictPaths.Any(path => !resolutions.ContainsKey(path)))
            throw new ArgumentException("Choose a resolution for every migration conflict.");
        return resolutions;
    }

    private async Task<JsonNode?> PreflightCurseForgeModpackAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var invalidation = curseForgePreflightEvidence.InvalidateWithEvidence();
        await RevokeCurseForgePreflightAsync(invalidation.OperationId).ConfigureAwait(true);
        var evidenceGeneration = invalidation.Generation;
        if (!Guid.TryParse(RequiredString(parameters, "reviewId", 64), out var reviewId) ||
            reviewId == Guid.Empty)
            throw new ArgumentException("The CurseForge selection review identity is invalid.");
        var selectionMethod = CurseForgePreflightEvidence.RequireSelectionMethod(
            RequiredString(parameters, "modpackSelectionMethod", 16));
        var projectId = RequiredString(parameters, "projectId", 80);
        var versionId = RequiredString(parameters, "versionId", 80);
        var key = CatalogKey(CatalogProvider.CurseForge, projectId);
        if (!modpackCatalog.TryGetValue(key, out var project) ||
            project.Provider != CatalogProvider.CurseForge)
            throw new ArgumentException("Refresh the CurseForge catalog before inspecting this release.");
        var release = project.Versions.FirstOrDefault(candidate =>
            candidate.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase));
        if (release is null || !release.ClientFileId.Equals(versionId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The exact CurseForge client release is no longer selected.");
        if (!(release.HasServerPackage && release.Sha1.Length == 40 && release.SizeBytes is > 0) &&
            !(release.CanGenerateServerCandidate && release.ClientSha1.Length == 40 &&
              release.ClientSizeBytes is > 0))
            throw new ArgumentException("This exact CurseForge release has no integrity-verifiable server path.");

        var preflightOperationId = Guid.NewGuid();
        await using var revocation = new CurseForgePreflightRevocationLease(
            preflightOperationId,
            async operationId =>
            {
                curseForgePreflightEvidence.InvalidateIfOperationId(operationId);
                await RevokeCurseForgePreflightAsync(operationId).ConfigureAwait(true);
            });
        var result = await client.SendAsync<CurseForgeModpackPreflightResult>(
            "PreflightCurseForgeModpack",
            new CurseForgeModpackPreflightRequest(
                preflightOperationId, project.ProjectId, release.ClientFileId, release.ServerPackFileId),
            cancellationToken).ConfigureAwait(true);
        if (!result.ProjectId.Equals(project.ProjectId, StringComparison.Ordinal) ||
            !result.ClientFileId.Equals(release.ClientFileId, StringComparison.Ordinal) ||
            !result.ServerPackFileId.Equals(release.ServerPackFileId, StringComparison.Ordinal) ||
            result.OperationId != preflightOperationId)
            throw new InvalidDataException("CurseForge preflight returned a contradictory release identity.");
        if (result.State != CatalogReleasePreflightState.Ready)
            revocation.Complete();

        cancellationToken.ThrowIfCancellationRequested();
        CurseForgePreflightEvidence? evidence = null;
        if (result.State == CatalogReleasePreflightState.Ready &&
            !curseForgePreflightEvidence.TryCommit(
                evidenceGeneration, reviewId, selectionMethod, project, release, result,
                DateTimeOffset.UtcNow, out evidence))
        {
            throw new InvalidOperationException(
                "The selected CurseForge release changed while inspection was running. Inspect it again.");
        }
        if (result.State != CatalogReleasePreflightState.Ready &&
            !curseForgePreflightEvidence.IsCurrent(evidenceGeneration))
        {
            throw new InvalidOperationException(
                "The selected CurseForge release changed while inspection was running. Inspect it again.");
        }

        var updated = (evidence?.ApplyTo(release) ?? release with
        {
            ClientDownloadUrl = result.ClientDownloadUrl,
            ClientSha1 = result.ClientSha1,
            ClientSha256 = result.ClientSha256,
            ClientSizeBytes = result.ClientSizeBytes
        }) with
        {
            CreationPreflightState = result.State,
            CreationPreflightDetail = result.Detail
        };
        project = project with
        {
            Versions = project.Versions.Select(candidate =>
                candidate.VersionId.Equals(updated.VersionId, StringComparison.OrdinalIgnoreCase)
                    ? updated : candidate).ToArray()
        };
        modpackCatalog[key] = project;
        cancellationToken.ThrowIfCancellationRequested();
        var response = JsonSerializer.SerializeToNode(
            ToWebModpackRelease(project, updated), WebUiProtocol.Json);
        revocation.Complete();
        return response;
    }

    private async Task InvalidateCurseForgePreflightAsync()
    {
        var invalidation = curseForgePreflightEvidence.InvalidateWithEvidence();
        await RevokeCurseForgePreflightAsync(invalidation.OperationId).ConfigureAwait(true);
    }

    private async Task RevokeCurseForgePreflightAsync(Guid? operationId)
    {
        if (operationId is not { } exactOperationId || exactOperationId == Guid.Empty)
            return;
        try
        {
            _ = await client.SendAsync<OperationResult>(
                "RevokeCurseForgeModpackPreflight",
                new RevokeCurseForgeModpackPreflightRequest(exactOperationId),
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Revocation is exact and idempotent. If the Agent is reconnecting, its short monotonic
            // authorization lifetime remains the fail-closed boundary and no creation can use a
            // locally invalidated review.
        }
    }

    private async Task InvalidateCurseForgeContentPlanForServerChangeAsync(Guid? selectedServerId)
    {
        var invalidation = curseForgeContentPlanEvidence.InvalidateForServerChange(selectedServerId);
        await RevokeCurseForgeContentPlanAsync(invalidation.AuthorizationId).ConfigureAwait(true);
    }

    private async Task RevokeCurseForgeContentPlanAsync(Guid? authorizationId)
    {
        if (authorizationId is not { } exactAuthorizationId || exactAuthorizationId == Guid.Empty)
            return;
        try
        {
            _ = await client.SendAsync<OperationResult>(
                "RevokeCurseForgeManagedContentPlan",
                new RevokeManagedContentPlanAuthorizationRequest(exactAuthorizationId),
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The Agent authorization remains short-lived and one-time if it is reconnecting.
            // Local evidence is already gone, so the renderer cannot submit it through this App.
        }
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
            item.ServerPathChecked,
            serverSupport = item.InstallationSupport.ToString(),
            clientRequirement = item.ClientRequirement.ToString(),
            trend = new { available = false, detail = "No local period snapshot history exists yet." },
            versions = item.Versions.Select(version => ToWebModpackRelease(item, version)).ToArray()
        };

    private static object ToWebModpackRelease(CatalogItem item, CatalogVersion version)
    {
        var official = version.HasServerPackage &&
                        (item.Provider != CatalogProvider.CurseForge || version.CurseForgeInstallRoute == CurseForgeInstallRoute.OfficialServerPack) && version.Sha1.Length == 40 &&
                        version.SizeBytes is > 0 &&
                        (item.Provider == CatalogProvider.CurseForge || version.Sha512.Length == 128);
        var generated = item.Provider == CatalogProvider.CurseForge && version.CurseForgeInstallRoute == CurseForgeInstallRoute.GeneratedCandidate && version.CanGenerateServerCandidate &&
                        version.ClientSha1.Length == 40 && version.ClientSizeBytes is > 0;
        var serverPathAvailable = official || generated;
        var preflightReady = item.Provider != CatalogProvider.CurseForge ||
                             version.CreationPreflightState == CatalogReleasePreflightState.Ready;
        var canCreate = serverPathAvailable && preflightReady;
        var limitation = item.Provider == CatalogProvider.CurseForge && serverPathAvailable && !preflightReady
            ? version.CreationPreflightState == CatalogReleasePreflightState.Unsupported
                ? version.CreationPreflightDetail
                : "Inspecting the exact client manifest is required before this release can be created."
            : canCreate ? "" : !version.HasServerPackage && !version.CanGenerateServerCandidate
                ? "No supportable official server pack or exact generated-server input was found."
                : "This release is missing the complete integrity metadata required for managed creation.";
        return new
        {
            version.VersionId,
            version.VersionName,
            version.MinecraftVersion,
            version.Loader,
            version.LoaderVersion,
            releaseChannel = version.ReleaseChannel.ToString(),
            version.PublishedAt,
            sizeBytes = generated ? version.ClientSizeBytes : version.SizeBytes,
            version.Changelog,
            version.RequiredJavaMajor,
            version.ClientFileId,
            version.ServerPackFileId,
            installationRoute = item.Provider == CatalogProvider.CurseForge ? version.CurseForgeInstallRoute.ToString() : null,
            installationRouteDetail = version.InstallationRouteDetail,
            hasIntegrity = generated ? version.ClientSha1.Length == 40 :
                version.Sha1.Length == 40 &&
                (item.Provider == CatalogProvider.CurseForge || version.Sha512.Length == 128),
            canCreate,
            preflightState = version.CreationPreflightState.ToString(),
            preflightDetail = version.CreationPreflightDetail,
            serverPath = official ? "Official server pack" : generated
                ? "ChunkPilot can generate and validate a server candidate"
                : "No supportable server setup found",
            limitation
        };
    }

    private static string CatalogKey(CatalogProvider provider, string projectId) =>
        $"{provider}:{projectId}";

    private async Task<JsonNode?> LoadModpackImageAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CatalogProvider>(RequiredString(parameters, "provider", 32), true,
                out var provider) || provider is not (CatalogProvider.Modrinth or CatalogProvider.CurseForge))
            throw new ArgumentException("The modpack provider is invalid.");
        var projectId = RequiredString(parameters, "projectId", 80);
        if (!modpackCatalog.TryGetValue(CatalogKey(provider, projectId), out var item) ||
            string.IsNullOrWhiteSpace(item.IconUrl))
            return JsonSerializer.SerializeToNode(new { dataUrl = (string?)null }, WebUiProtocol.Json);
        if (!Uri.TryCreate(item.IconUrl, UriKind.Absolute, out var uri) ||
            !IsApprovedModpackImageUri(provider, uri))
            return JsonSerializer.SerializeToNode(new { dataUrl = (string?)null }, WebUiProtocol.Json);
        var dataUrl = await modpackImages.LoadAsync(provider, uri, cancellationToken).ConfigureAwait(true);
        return JsonSerializer.SerializeToNode(new { dataUrl }, WebUiProtocol.Json);
    }

    internal static bool IsApprovedModpackImageUri(CatalogProvider provider, Uri? uri)
    {
        return ModpackImageLoader.IsApprovedUri(provider, uri);
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

    private async Task<JsonNode?> ChooseCreationWorldAsync(JsonObject parameters)
    {
        var kindText = OptionalString(parameters, "kind", 16);
        var kind = kindText.Equals("folder", StringComparison.OrdinalIgnoreCase)
            ? CreationWorldSourceKind.Folder
            : kindText.Equals("zip", StringComparison.OrdinalIgnoreCase)
                ? CreationWorldSourceKind.ZipArchive
                : throw new ArgumentException("Choose either a world folder or a world ZIP.");
        string path;
        if (kind == CreationWorldSourceKind.Folder)
        {
            path = new DialogService().SelectFolder("Choose an existing Minecraft world folder") ?? "";
            if (string.IsNullOrWhiteSpace(path))
                return JsonSerializer.SerializeToNode(new { cancelled = true }, WebUiProtocol.Json);
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a ZIP containing one Minecraft world",
                Filter = "Minecraft world ZIP (*.zip)|*.zip",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true)
                return JsonSerializer.SerializeToNode(new { cancelled = true }, WebUiProtocol.Json);
            path = dialog.FileName;
        }
        var source = await creationWorldSources.InspectAsync(path, kind).ConfigureAwait(true);
        var token = worldSourceTokens.Issue(source);
        return JsonSerializer.SerializeToNode(new
        {
            cancelled = false,
            token.Token,
            token.DisplayName,
            kind = token.Kind.ToString(),
            token.WorldName,
            token.SourceSizeBytes,
            token.ExpandedSizeBytes,
            token.FileCount,
            token.IncludesNether,
            token.IncludesEnd,
            token.ExpiresAt
        }, WebUiProtocol.Json);
    }

    private JsonNode? BeginCreationOperation(JsonObject parameters)
    {
        var operationId = RequireClientOperationId(parameters, "creation");
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
        var initialWorld = await ConsumeInitialWorldAsync(parameters, cancellationToken).ConfigureAwait(true);
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
            InitialWorld = initialWorld,
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
        var creationStarted = false;
        Guid? exactCurseForgeAuthorization = null;
        try
        {
        if (parameters["eulaAccepted"]?.GetValue<bool>() is not true)
            throw new ArgumentException("You must deliberately accept the Minecraft EULA before creation.");
        if (parameters["experimentalAccepted"]?.GetValue<bool>() is not true)
            throw new ArgumentException(
                "Acknowledge that this exact modpack release will be validated on demand during creation.");
        var initialWorld = await ConsumeInitialWorldAsync(parameters, cancellationToken).ConfigureAwait(true);

        ModpackCreationPlan plan;
        (Guid ReviewId, string SelectionMethod, string ProjectId, string ClientFileId)? curseForgeReview = null;
        var localToken = OptionalString(parameters, "localPackToken", 128);
        if (!string.IsNullOrWhiteSpace(localToken))
        {
            await InvalidateCurseForgePreflightAsync().ConfigureAwait(true);
            var selected = localImportTokens.Consume(localToken);
            var reviewed = await client.SendAsync<ServerImportInspection>("InspectServerImport",
                new ServerImportInspectRequest(selected.Path), cancellationToken).ConfigureAwait(true);
            if (!reviewed.CanInstall)
                throw new ArgumentException(reviewed.Limitation);
            if (reviewed.SourceKind != selected.Inspection.SourceKind ||
                reviewed.SourceSizeBytes != selected.Inspection.SourceSizeBytes ||
                !reviewed.Sha256.Equals(selected.Inspection.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected local server source changed after review. Choose it again.");
            if (reviewed.SourceKind is not (ServerImportSourceKind.ModrinthPack or
                ServerImportSourceKind.CurseForgePack))
            {
                if (initialWorld is not null)
                    throw new ArgumentException("A complete imported server source already owns its world layout. Choose Create new world for that path, or create a managed server from a version or pack and upload the world there.");
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
            if (reviewed.SourceKind == ServerImportSourceKind.CurseForgePack)
            {
                plan = CommonPlan(new ModpackCreationPlan
                {
                    SourceKind = ModpackCreationSource.LocalCurseForgeManifest,
                    Source = selected.Path,
                    Provider = UpdateProvider.CurseForge,
                    ProjectName = reviewed.DisplayName,
                    VersionName = Path.GetFileNameWithoutExtension(selected.Path),
                    MinecraftVersion = reviewed.MinecraftVersion,
                    Loader = reviewed.Platform,
                    LoaderVersion = reviewed.LoaderVersion,
                    RequiredJavaMajor = reviewed.RequiredJavaMajor,
                    ExpectedSha256 = reviewed.Sha256,
                    ExpectedSizeBytes = reviewed.SourceSizeBytes
                });
            }
            else
            {
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
                    Loader = inspection.Loader,
                    LoaderVersion = inspection.LoaderVersion,
                    RequiredJavaMajor = inspection.RequiredJavaMajor,
                    ExpectedSha512 = inspection.ArchiveSha512,
                    ExpectedSizeBytes = inspection.ArchiveSizeBytes
                });
            }
        }
        else
        {
            if (!Enum.TryParse<CatalogProvider>(OptionalString(parameters, "modpackProvider", 32), true,
                    out var provider))
                provider = CatalogProvider.Modrinth;
            if (provider is not (CatalogProvider.Modrinth or CatalogProvider.CurseForge))
                throw new ArgumentException("Select a supported modpack provider.");
            var projectId = RequiredString(parameters, "modpackProjectId", 80);
            var versionId = RequiredString(parameters, "modpackVersionId", 80);
            if (provider == CatalogProvider.CurseForge)
            {
                if (!Guid.TryParse(RequiredString(parameters, "modpackReviewId", 64), out var reviewId) ||
                    reviewId == Guid.Empty)
                    throw new ArgumentException("The CurseForge selection review identity is invalid.");
                var selectionMethod = CurseForgePreflightEvidence.RequireSelectionMethod(
                    RequiredString(parameters, "modpackSelectionMethod", 16));
                var preflightEvidence = curseForgePreflightEvidence.Require(
                    reviewId, selectionMethod, projectId, versionId, DateTimeOffset.UtcNow);
                exactCurseForgeAuthorization = preflightEvidence.OperationId;
                curseForgeReview = (reviewId, selectionMethod, projectId, versionId);
                plan = preflightEvidence.Bind(CommonPlan(new ModpackCreationPlan()));
            }
            else
            {
                await InvalidateCurseForgePreflightAsync().ConfigureAwait(true);
                if (!modpackCatalog.TryGetValue(CatalogKey(provider, projectId), out var project))
                    throw new ArgumentException("Refresh the selected provider catalog before creating this pack.");
                var release = project.Versions.FirstOrDefault(version =>
                    version.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase) &&
                    version.HasServerPackage && version.Sha1.Length == 40 &&
                    version.Sha512.Length == 128 && version.SizeBytes is > 0)
                    ?? throw new ArgumentException(
                        "Select an exact integrity-verifiable provider release with a supportable server path.");
                plan = CommonPlan(new ModpackCreationPlan
                {
                    SourceKind = ModpackCreationSource.Modrinth,
                    Source = release.DownloadUrl,
                    Provider = UpdateProvider.Modrinth,
                    ProjectId = project.ProjectId,
                    ProjectSlug = project.Slug,
                    ProjectName = project.Name,
                    VersionId = release.VersionId,
                    VersionName = release.VersionName,
                    ReleaseChannel = release.ReleaseChannel,
                    MinecraftVersion = release.MinecraftVersion,
                    Loader = release.Loader,
                    LoaderVersion = release.LoaderVersion,
                    RequiredJavaMajor = release.RequiredJavaMajor > 0
                        ? release.RequiredJavaMajor
                        : JavaRuntimePolicy.RequiredMajorForMinecraft(release.MinecraftVersion),
                    ExpectedSha1 = release.Sha1,
                    ExpectedSha512 = release.Sha512,
                    ExpectedSizeBytes = release.SizeBytes
                });
            }
        }

        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems));
        if (curseForgeReview is { } review)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var consumed = curseForgePreflightEvidence.Consume(
                review.ReviewId, review.SelectionMethod, review.ProjectId, review.ClientFileId,
                DateTimeOffset.UtcNow);
            if (consumed.OperationId != plan.PreflightOperationId)
                throw new InvalidOperationException(
                    "The CurseForge preflight authorization changed before creation could begin.");
            exactCurseForgeAuthorization = consumed.OperationId;
            plan = consumed.Bind(plan);
        }
        var startedOperationId = await AgentOperationAcceptance.BeginModpackCreationAsync(
            plan,
            token => client.SendAsync<InstallOperationRequest>(
                "BeginModpackCreation", new BeginModpackCreationRequest(plan), token),
            token => client.SendAsync<ModpackCreationCancellationFenceResult>(
                "CancelOrFenceModpackCreation",
                new CancelOrFenceModpackCreationRequest(plan),
                token),
            cancellationToken).ConfigureAwait(true);
        creationStarted = true;
        exactCurseForgeAuthorization = null;
        return startedOperationId;

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
            ExperimentalRuntimeRiskAccepted = true,
            InitialWorld = initialWorld
        };
        }
        catch (StaleCurseForgePreflightException exception)
        {
            exactCurseForgeAuthorization ??= exception.OperationId;
            throw;
        }
        finally
        {
            if (!creationStarted && exactCurseForgeAuthorization is { } operationId)
            {
                curseForgePreflightEvidence.InvalidateIfOperationId(operationId);
                await RevokeCurseForgePreflightAsync(operationId).ConfigureAwait(true);
            }
        }
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
        var initialWorld = await ConsumeInitialWorldAsync(parameters, cancellationToken).ConfigureAwait(true);
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
                                              parameters["experimentalAccepted"]?.GetValue<bool>() is true,
            InitialWorld = initialWorld
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
        var initialWorld = await ConsumeInitialWorldAsync(parameters, cancellationToken).ConfigureAwait(true);

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
                                                parameters["experimentalAccepted"]?.GetValue<bool>() is true,
            InitialWorld = initialWorld
        };
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems));
        return await paperCreation.BeginAsync(plan, cancellationToken).ConfigureAwait(true);
    }

    private async Task<CreationWorldSource?> ConsumeInitialWorldAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var token = OptionalString(parameters, "initialWorldToken", 128);
        return string.IsNullOrWhiteSpace(token)
            ? null
            : await worldSourceTokens.ConsumeAsync(token, cancellationToken).ConfigureAwait(true);
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
            progress.Progress.IsIndeterminate,
            progress.Progress.StageElapsedSeconds,
            progress.Progress.LastMeaningfulStatus,
            progress.Progress.SecondsSinceMeaningfulUpdate,
            progress.Progress.RecentStatus,
            progress.Progress.NewLogOutputObserved,
            progress.CanRetry, progress.CanDiscard, progress.RetryGeneration, progress.RetainedInputBytes,
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
                progress.Progress.IsIndeterminate,
                progress.Progress.StageElapsedSeconds,
                progress.Progress.LastMeaningfulStatus,
                progress.Progress.SecondsSinceMeaningfulUpdate,
                progress.Progress.RecentStatus,
                progress.Progress.NewLogOutputObserved,
                progress.CanRetry, progress.CanDiscard, progress.RetryGeneration, progress.RetainedInputBytes,
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

    private async Task<JsonNode?> RecoverCreationAsync(JsonObject parameters, bool retry)
    {
        if (!Guid.TryParse(RequiredString(parameters, "operationId", 64), out var operationId))
            throw new ArgumentException("A valid creation operation ID is required.");
        var generation = parameters["retryGeneration"]?.GetValue<int>()
            ?? throw new ArgumentException("The current recovery generation is required.");
        var request = new CreationRecoveryRequest(operationId, generation);
        if (retry)
            await client.SendAsync<InstallOperationRequest>("RetryModpackCreation", request).ConfigureAwait(true);
        else
            await client.SendAsync<OperationResult>("DiscardModpackCreation", request).ConfigureAwait(true);
        return JsonSerializer.SerializeToNode(new { accepted = true, operationId }, WebUiProtocol.Json);
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

    private async Task<JsonNode> ChooseAppearanceIconAsync(ServerDefinition server)
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
        using var image = await ImageSharpImage.LoadAsync<Rgba32>(new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = 1 }, source).ConfigureAwait(true);
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
        if (viewModel.SelectedServer?.Definition.Id != server.Id)
            throw new InvalidOperationException("The selected server changed while the image was opening. The new server was not modified.");
        return JsonSerializer.SerializeToNode(iconEdits.Select(server, preview.ToArray(), Path.GetFileName(dialog.FileName)), WebUiProtocol.Json)!;
    }

    private async Task InstallAppearanceIconAsync(ServerSnapshot server, string base64, string? expectedIconSha256 = null)
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
                new IconInstallRequest(server.Definition.Id, path, SaveToLibrary: true, ExpectedIconSha256: expectedIconSha256)).ConfigureAwait(true);
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

    private async Task<JsonNode?> RollbackVersionFromWebUiAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var (serverId, snapshotId) = ValidateRollbackParameters(parameters);
        RequireSelectedRollbackServer(serverId, viewModel.SelectedServer?.Definition.Id);
        SelectVersion(parameters, requireRollbackReady: true);
        var result = await viewModel.RollbackVersionFromWebUiAsync(
            serverId, snapshotId, cancellationToken).ConfigureAwait(true);
        await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        if (bridge is { } currentBridge)
            await currentBridge.PublishSnapshotAsync().ConfigureAwait(true);
        return JsonSerializer.SerializeToNode(new
        {
            accepted = true,
            serverId,
            versionId = snapshotId,
            result.Message
        }, WebUiProtocol.Json);
    }

    private async Task<JsonNode?> MarkVersionHealthyFromWebUiAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var (serverId, snapshotId) = ValidateMarkHealthyParameters(parameters);
        _ = RequireSelectedPendingValidation(
            serverId, snapshotId, viewModel.SelectedServer?.Definition.Id, viewModel.Versions);
        var result = await viewModel.MarkVersionHealthyFromWebUiAsync(
            serverId, snapshotId, retentionDays: 30, cancellationToken).ConfigureAwait(true);
        if (bridge is { } currentBridge)
            await currentBridge.PublishSnapshotAsync().ConfigureAwait(true);
        return JsonSerializer.SerializeToNode(new
        {
            accepted = true,
            serverId,
            versionId = snapshotId,
            result.Message
        }, WebUiProtocol.Json);
    }

    internal static (Guid ServerId, Guid SnapshotId) ValidateMarkHealthyParameters(JsonObject parameters)
    {
        if (!RequiredBool(parameters, "confirmed"))
            throw new ArgumentException("Marking an update healthy requires deliberate in-product confirmation.");
        if (!Guid.TryParse(RequiredString(parameters, "serverId", 64), out var serverId))
            throw new ArgumentException("A valid server ID is required.");
        if (!Guid.TryParse(RequiredString(parameters, "versionId", 64), out var snapshotId))
            throw new ArgumentException("A valid version ID is required.");
        return (serverId, snapshotId);
    }

    internal static VersionSnapshot RequireSelectedPendingValidation(
        Guid requestedServerId,
        Guid snapshotId,
        Guid? selectedServerId,
        IEnumerable<VersionSnapshot> versions)
    {
        if (selectedServerId != requestedServerId)
            throw new InvalidOperationException(
                "The selected server changed before validation could be saved. No other server was modified.");
        return versions.FirstOrDefault(version => version.ServerId == requestedServerId &&
                   version.Id == snapshotId && version.IsActive &&
                   version.Health == VersionHealth.PendingValidation)
               ?? throw new InvalidOperationException(
                   "That active version is no longer awaiting validation. Refresh the server before trying again.");
    }

    internal static (Guid ServerId, Guid SnapshotId) ValidateRollbackParameters(JsonObject parameters)
    {
        if (!RequiredBool(parameters, "confirmed"))
            throw new ArgumentException("Version rollback requires deliberate in-product confirmation.");
        if (!Guid.TryParse(RequiredString(parameters, "serverId", 64), out var serverId))
            throw new ArgumentException("A valid server ID is required.");
        if (!Guid.TryParse(RequiredString(parameters, "versionId", 64), out var snapshotId))
            throw new ArgumentException("A valid version ID is required.");
        return (serverId, snapshotId);
    }

    internal static void RequireSelectedRollbackServer(Guid requestedServerId, Guid? selectedServerId)
    {
        if (selectedServerId != requestedServerId)
            throw new InvalidOperationException(
                "The selected server changed before rollback could start. No version was restored.");
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

    internal static Guid RequireClientOperationId(JsonObject values, string operationKind)
    {
        if (!Guid.TryParse(RequiredString(values, "operationId", 64), out var operationId) ||
            operationId == Guid.Empty)
            throw new ArgumentException(
                $"A non-empty client-generated {operationKind} operation ID is required.");
        return operationId;
    }

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

    private static PluginProviderKind ParseAddonProvider(JsonObject values)
    {
        var raw = OptionalString(values, "provider", 32);
        if (string.IsNullOrWhiteSpace(raw)) return PluginProviderKind.Modrinth;
        if (!Enum.TryParse<PluginProviderKind>(raw, true, out var provider) ||
            provider is not (PluginProviderKind.Modrinth or PluginProviderKind.CurseForge))
            throw new ArgumentException("The add-on provider is invalid.");
        return provider;
    }

    internal static ManagedContentPlanAuthorization? ParseManagedContentPlanAuthorization(
        JsonObject parameters)
    {
        if (!parameters.TryGetPropertyValue("planAuthorization", out var node) || node is null)
            return null;
        if (node is not JsonObject authorization ||
            authorization.Count != 2 ||
            authorization.Any(pair => pair.Key is not ("authorizationId" or "digest")))
            throw new ArgumentException(
                "The dependency-plan authorization must contain only an authorizationId and digest.");

        var authorizationIdText = AuthorizationText("authorizationId", 64);
        if (!Guid.TryParse(authorizationIdText, out var authorizationId) || authorizationId == Guid.Empty)
            throw new ArgumentException("The dependency-plan authorization identity is invalid.");
        var digest = AuthorizationText("digest", 64);
        if (digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The dependency-plan authorization digest is invalid.");

        return new ManagedContentPlanAuthorization
        {
            AuthorizationId = authorizationId,
            Digest = digest.ToLowerInvariant()
        };

        string AuthorizationText(string name, int maximumLength)
        {
            if (authorization[name] is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                string.IsNullOrWhiteSpace(text))
                throw new ArgumentException($"The dependency-plan {name} is required.");
            text = text.Trim();
            if (text.Length > maximumLength)
                throw new ArgumentException($"The dependency-plan {name} is too long.");
            return text;
        }
    }

    private static Guid? TryReadManagedContentPlanAuthorizationId(JsonObject parameters)
    {
        if (parameters["planAuthorization"] is not JsonObject authorization ||
            authorization["authorizationId"] is not JsonValue value ||
            !value.TryGetValue<string>(out var text) ||
            !Guid.TryParse(text, out var authorizationId) || authorizationId == Guid.Empty)
            return null;
        return authorizationId;
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
        var planInvalidation = curseForgeContentPlanEvidence.InvalidateWithEvidence();
        if (planInvalidation.AuthorizationId is { } authorizationId)
            client.TrySendOneWay(
                "RevokeCurseForgeManagedContentPlan",
                new RevokeManagedContentPlanAuthorizationRequest(authorizationId));
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
        worldSourceTokens.Clear();
        bridge?.Dispose();
        modpackImages.Dispose();
        playerHeads.Dispose();
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
