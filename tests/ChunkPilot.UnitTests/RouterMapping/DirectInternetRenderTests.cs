using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunkPilot.App;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// Renders the real Overview page, with the real design system and the real ViewModel, in every Direct
/// internet state and at every reviewed width.
/// </summary>
/// <remarks>
/// <para>
/// This is the runtime check behind the visual review: the page is measured, arranged and rasterised by
/// WPF itself, so a template that fails to resolve, a control that collapses to nothing, or content that
/// overflows its column shows up here rather than in a screenshot nobody took. The PNGs are written to
/// <c>artifacts/friend-connectivity-router-mapping/</c> and are the artifacts referenced by the
/// completion report.
/// </para>
/// <para>
/// The state comes from a controlled fake agent. Nothing here contacts a router.
/// </para>
/// </remarks>
public sealed class DirectInternetRenderTests
{
    private static readonly Guid ServerId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly (string Name, int Width, int Height)[] Sizes =
    [
        ("800x600", 800, 600),
        ("1000x700", 1000, 700),
        ("1440x900", 1440, 900),
        ("maximized-1920x1080", 1920, 1080)
    ];

    public static TheoryData<string> StateNames()
    {
        var data = new TheoryData<string>();
        foreach (var state in States().Keys)
            data.Add(state);
        return data;
    }

    [Theory]
    [MemberData(nameof(StateNames))]
    public void Every_direct_internet_state_renders_at_every_reviewed_width(string stateName)
    {
        var state = States()[stateName];
        foreach (var (sizeName, width, height) in Sizes)
        {
            var bitmap = Render(state, width, height);

            Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0,
                $"{stateName} at {sizeName} produced no pixels.");
            Assert.True(HasVisibleContent(bitmap), $"{stateName} at {sizeName} rendered a blank surface.");
            Save(bitmap, $"{stateName}-{sizeName}.png");
        }
    }

    /// <summary>Primary content must never need horizontal scrolling, at any reviewed width.</summary>
    [Theory]
    [MemberData(nameof(StateNames))]
    public void No_state_overflows_its_width(string stateName)
    {
        var state = States()[stateName];
        foreach (var (sizeName, width, height) in Sizes)
        {
            var measured = MeasuredWidth(state, width, height);
            Assert.True(measured <= width + 1,
                $"{stateName} at {sizeName} wanted {measured:0} px of width inside {width} px.");
        }
    }

    private static Dictionary<string, RouterMappingState> States() => new(StringComparer.Ordinal)
    {
        ["off"] = Base(RouterMappingPhase.Off),
        ["discovering"] = Base(RouterMappingPhase.Checking),
        ["supported"] = Base(RouterMappingPhase.Supported),
        // The real-router acceptance run, as it should now read: the router answered, nothing is
        // exposed yet, and every technical row describes what was actually learned.
        ["router-answered"] = RealRouterCheck(),
        // The same run if the router had gone on to refuse the mapping.
        ["mapping-rejected"] = RealRouterCheck() with
        {
            Phase = RouterMappingPhase.NeedsAttention,
            Enabled = true,
            ConsentGranted = true,
            Failure = RouterMappingFailure.RequestRejected,
            LastOperationDetail = "UPnP AddPortMapping failed for TCP 25565 with error 402 (InvalidArgs)."
        },
        ["creating"] = Base(RouterMappingPhase.Creating, enabled: true),
        ["active"] = Base(RouterMappingPhase.Active, enabled: true, external: "203.0.113.7"),
        // The stopped server that used to keep reporting "Router port is open".
        ["stopped-configured"] = StoppedAfterCleanup(),
        // The same stop, when the router would not confirm the cleanup.
        ["cleanup-failed"] = StoppedAfterCleanup() with
        {
            Phase = RouterMappingPhase.NeedsAttention,
            RemovalPending = true,
            Failure = RouterMappingFailure.RemovalFailed,
            LastOperationDetail = "UPnP DeletePortMapping failed with error 501 (ActionFailed)."
        },
        ["conflict"] = Base(RouterMappingPhase.Conflict, enabled: true) with
        {
            Failure = RouterMappingFailure.ForeignMappingPresent,
            LastOperationDetail =
                "The router already forwards public port 25565 to 192.168.1.90:25565 (Living room console)."
        },
        ["upstream-nat"] = Base(RouterMappingPhase.Active, enabled: true, external: "100.72.4.9"),
        ["unavailable"] = Base(RouterMappingPhase.Unavailable, enabled: true) with
        {
            Failure = RouterMappingFailure.MechanismUnsupported,
            LastOperationDetail = "[UpnpIgd] The gateway answered SSDP but published no WAN connection service."
        },
        ["timeout"] = Base(RouterMappingPhase.Undetermined, enabled: true) with
        {
            Failure = RouterMappingFailure.GatewayDidNotRespond,
            LastOperationDetail = "The gateway did not answer a PCP ANNOUNCE within the bounded retry window."
        },
        ["removing"] = Base(RouterMappingPhase.Removing, enabled: true),
        ["reconciling"] = Base(RouterMappingPhase.Reconciling, enabled: true),
        ["needs-attention"] = Base(RouterMappingPhase.NeedsAttention, enabled: true) with
        {
            RemovalPending = true,
            Failure = RouterMappingFailure.RemovalFailed,
            LastOperationDetail = "UPnP DeletePortMapping failed with error 501 (ActionFailed)."
        }
    };

    /// <summary>
    /// The real 25566 mapping after a deliberate Stop withdrew it: Direct internet still set up, no
    /// mapping open, no lease.
    /// </summary>
    private static RouterMappingState StoppedAfterCleanup() => new()
    {
        ServerId = ServerId,
        Enabled = true,
        ConsentGranted = true,
        Phase = RouterMappingPhase.Inactive,
        Mechanism = RouterMappingMechanism.UpnpIgd,
        AvailableMechanism = RouterMappingMechanism.UpnpIgd,
        Transport = MappingTransport.Tcp,
        GatewayAddress = "10.0.0.1",
        CandidateInternalClient = "10.0.0.140",
        InternalPort = 25566,
        ExternalPort = 25566,
        RouterReportedExternalAddress = "73.203.43.174",
        RouterReportedAddressClass = RoutableAddressClass.GloballyRoutable,
        LeaseIsFinite = true,
        LastCheckedAt = DateTimeOffset.Now,
        LastOperationDetail = "UPnP DeletePortMapping removed TCP 25566."
    };

    /// <summary>The user's real topology and the answer their router actually gave.</summary>
    private static RouterMappingState RealRouterCheck() => new()
    {
        ServerId = ServerId,
        Phase = RouterMappingPhase.Supported,
        Mechanism = RouterMappingMechanism.None,
        AvailableMechanism = RouterMappingMechanism.UpnpIgd,
        Transport = MappingTransport.Tcp,
        GatewayAddress = "10.0.0.1",
        CandidateInternalClient = "10.0.0.23",
        InternalPort = 25565,
        RouterReportedExternalAddress = "73.203.43.174",
        RouterReportedAddressClass = RoutableAddressClass.GloballyRoutable,
        LeaseIsFinite = true,
        LastCheckedAt = DateTimeOffset.Now,
        LastOperationDetail =
            "UPnP urn:schemas-upnp-org:service:WANIPConnection:1 answered at " +
            "http://10.0.0.1:49152/upnp/control/WANIPConnection0 and reported external address 73.203.43.174."
    };

    private static RouterMappingState Base(
        RouterMappingPhase phase, bool enabled = false, string external = "")
    {
        var assessment = RouterMappingPolicy.ClassifyExternalAddress(external);
        return new RouterMappingState
        {
            ServerId = ServerId,
            Enabled = enabled,
            ConsentGranted = enabled,
            Phase = phase,
            Mechanism = enabled ? RouterMappingMechanism.UpnpIgd : RouterMappingMechanism.None,
            Transport = MappingTransport.Tcp,
            GatewayAddress = "192.168.1.1",
            InternalClient = "192.168.1.50",
            InternalPort = 25565,
            ExternalPort = enabled ? 25565 : 0,
            RouterReportedExternalAddress = external,
            RouterReportedAddressClass = assessment.Class,
            UpstreamNatSuspected = assessment.SuggestsUpstreamNat,
            LeaseIsFinite = true,
            LeaseExpiresAt = enabled ? DateTimeOffset.Now.AddMinutes(58) : null,
            LastCheckedAt = DateTimeOffset.Now,
            LastOperationDetail = enabled
                ? "UPnP AddPortMapping accepted TCP 25565 to 192.168.1.50:25565 for 3600 seconds."
                : ""
        };
    }

    private static BitmapSource Render(RouterMappingState state, int width, int height) =>
        WpfDesignSystemHost.Run(() =>
        {
            var window = BuildWindow(state, width, height, out _);
            try
            {
                var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                target.Render(window);
                target.Freeze();
                return (BitmapSource)target;
            }
            finally
            {
                window.Close();
            }
        });

    private static double MeasuredWidth(RouterMappingState state, int width, int height) =>
        WpfDesignSystemHost.Run(() =>
        {
            var window = BuildWindow(state, width, height, out var page);
            try
            {
                return page.DesiredSize.Width;
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>
    /// Registers the application-level value converters. <c>AppTheme.Initialize</c> loads the design
    /// system, but the converters live directly in <c>App.xaml</c>; they are read from that same file
    /// here so the render harness can never drift from the keys the running application declares.
    /// </summary>
    private static void EnsureConverters()
    {
        if (Application.Current.Resources.Contains("BoolVisibility"))
            return;
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(DesignSystemFiles.AppProjectDirectory, "App.xaml"));
        var assembly = typeof(MainViewModel).Assembly;
        foreach (var element in xaml.Descendants()
                     .Where(node => node.Name.NamespaceName == "clr-namespace:ChunkPilot.App"))
        {
            var key = element.Attribute(DesignSystemFiles.XamlNamespace + "Key")?.Value;
            var type = assembly.GetType($"ChunkPilot.App.{element.Name.LocalName}");
            if (key is null || type is null)
                continue;
            Application.Current.Resources[key] = Activator.CreateInstance(type);
        }
    }

    private static Window BuildWindow(
        RouterMappingState state, int width, int height, out FrameworkElement page)
    {
        EnsureConverters();
        var model = ReadyModel();
        model.SelectedNetworkMode = NetworkMode.PortForwarding;
        model.RouterMapping = state;
        model.ShowsDirectInternetTechnicalDetails = true;
        if (state.Phase == RouterMappingPhase.Supported)
        {
            // The confirmation state is what the user sees between "the router can" and "do it".
            model.DirectInternetConsentPoints =
                ChunkPilot.App.Presentation.DirectInternetPresentation.ConsentPoints(state.InternalPort);
            model.ShowsDirectInternetConsent = true;
        }

        var content = new ServerOverviewPage();
        var window = new Window
        {
            DataContext = model,
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -10_000,
            Top = -10_000,
            Background = (Brush?)Application.Current.TryFindResource("AppSurfaceBase") ?? Brushes.Black,
            Content = new System.Windows.Controls.ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
            }
        };
        AppLayout.SetMode(window, AppLayout.ModeForWidth(width, window));
        window.Show();
        window.UpdateLayout();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
        // Scroll to what is under review, exactly as a reviewer would. Without this the block sits
        // below the fold at the smaller heights and the capture proves nothing about it.
        if (content.FindName("DirectInternetSection") is FrameworkElement section)
        {
            section.BringIntoView();
            window.UpdateLayout();
        }
        page = content;
        return window;
    }

    /// <summary>The Direct internet block, once scrolled to, must be on screen and have real size.</summary>
    [Theory]
    [MemberData(nameof(StateNames))]
    public void The_direct_internet_block_is_on_screen_at_every_reviewed_width(string stateName)
    {
        var state = States()[stateName];
        foreach (var (sizeName, width, height) in Sizes)
        {
            var bounds = WpfDesignSystemHost.Run(() =>
            {
                var window = BuildWindow(state, width, height, out var page);
                try
                {
                    var section = (FrameworkElement)page.FindName("DirectInternetSection")!;
                    var origin = section.TransformToAncestor(window).Transform(new Point(0, 0));
                    return new Rect(origin, new Size(section.ActualWidth, section.ActualHeight));
                }
                finally
                {
                    window.Close();
                }
            });

            Assert.True(bounds.Width > 0 && bounds.Height > 0,
                $"{stateName} at {sizeName}: the Direct internet block collapsed to nothing.");
            Assert.True(bounds.Right <= width + 1,
                $"{stateName} at {sizeName}: the block runs {bounds.Right - width:0} px past the window edge.");
            Assert.True(bounds.Top < height,
                $"{stateName} at {sizeName}: the block never came into view.");
        }
    }

    private static MainViewModel ReadyModel()
    {
        var model = new MainViewModel(new RenderFakeClient(ServerId), new SilentDialogs());
        model.InitializeAsync().GetAwaiter().GetResult();
        model.SelectedServer = model.Servers[0];
        return model;
    }

    private static bool HasVisibleContent(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var distinct = new HashSet<uint>();
        for (var index = 0; index + 3 < pixels.Length; index += 4 * 37)
        {
            distinct.Add(BitConverter.ToUInt32(pixels, index));
            if (distinct.Count > 8)
                return true;
        }
        return false;
    }

    private static void Save(BitmapSource bitmap, string fileName)
    {
        var directory = Path.Combine(DesignSystemFiles.RepositoryRoot, "artifacts",
            "friend-connectivity-router-mapping");
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    private sealed class RenderFakeClient(Guid serverId) : IAgentClient
    {
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default)
        {
            object response = operation switch
            {
                "Dashboard" => new DashboardSnapshot
                {
                    AgentConnected = true,
                    Host = new HostSnapshot { LanAddress = "192.168.1.50" },
                    Servers =
                    [
                        new ServerSnapshot
                        {
                            Definition = new ServerDefinition
                            {
                                Id = serverId,
                                Name = "Sunday survival",
                                RootPath = @"C:\fixture",
                                Port = 25565
                            },
                            State = ServerState.Running,
                            OnlinePlayers = 2,
                            MaxPlayers = 8
                        }
                    ]
                },
                "GetRouterMapping" or "CheckRouterMapping" or "EnableRouterMapping" or
                    "DisableRouterMapping" or "CancelRouterMapping" => new RouterMappingState
                    {
                        ServerId = serverId,
                        InternalPort = 25565
                    },
                "GetExternalReachability" or "CancelExternalReachability" =>
                    new ExternalReachabilityState { ServerId = serverId },
                "GetCapabilities" => new ServerCapabilityProfile(),
                "GetNetworkConfiguration" => new NetworkConfiguration(),
                "ListBackups" => Array.Empty<BackupRecord>(),
                "ListSchedules" => Array.Empty<ScheduleEntry>(),
                "ListFiles" => Array.Empty<FileSystemEntry>(),
                "Inventory" => Array.Empty<ModPluginEntry>(),
                "Diagnostics" => Array.Empty<DiagnosticFinding>(),
                "ListWorlds" => Array.Empty<WorldEntry>(),
                "ListWhitelist" => Array.Empty<WhitelistEntry>(),
                "ListPlayerAccess" => Array.Empty<UnifiedPlayerAccess>(),
                "GetPlayerAccess" => new PlayerAccessSnapshot(),
                "ReadGamerules" => new GameruleStateResponse(),
                "ListAutomationRecipes" => Array.Empty<AutomationRecipe>(),
                "GetCrossplayConfiguration" => new CrossplayConfiguration(),
                "ListDatapacks" => Array.Empty<DatapackInventoryItem>(),
                "GetResourcePackConfiguration" => new ResourcePackConfiguration(),
                "GetSetting" => new TextResponse(""),
                "GetUpdateSource" => (object?)null!,
                "GetUpdatePreferences" => new UpdatePreferences(),
                "ListVersions" => Array.Empty<VersionSnapshot>(),
                "ListUpdateHistory" => Array.Empty<UpdateHistoryEntry>(),
                _ => OperationResult.Ok("ok")
            };
            return Task.FromResult((TResponse)response);
        }
    }

    private sealed class SilentDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }
}
