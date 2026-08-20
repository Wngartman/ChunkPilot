using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.App;

/// <summary>
/// Renders the real Direct internet surface for review without an Agent, UAC, firewall access, router
/// contact, or any external service.
/// </summary>
/// <remarks>
/// Every scenario is invented data pushed straight into the ViewModel. No switch here can reach the
/// probe service, the router, or Windows Firewall, and none of them takes the single-instance lock.
/// </remarks>
internal static class FirewallReviewLauncher
{
    private const string Switch = "--render-firewall";
    private const string CorrelationSwitch = "--render-firewall-correlation";
    private const string CompatibilitySwitch = "--render-firewall-compatibility";
    private const string PolicyReadSwitch = "--render-firewall-policy-read";
    private const string ForeignCoverageSwitch = "--render-firewall-foreign-coverage";
    private const string ExternalReachabilitySwitch = "--render-external-reachability";

    private const string FirewallSection = "WindowsFirewallSection";
    private const string ExternalSection = "ExternalReachabilitySection";

    /// <summary>
    /// The review modes, in precedence order. Ordered rather than nested so adding one cannot change
    /// how an existing one behaves.
    /// </summary>
    private static readonly ReviewMode[] Modes =
    [
        new(ForeignCoverageSwitch, FirewallSection, ForeignCoverageScenarios, ExpandFirewallDetails: true,
            AllSizes: false),
        new(PolicyReadSwitch, FirewallSection, PolicyReadScenarios, ExpandFirewallDetails: true, AllSizes: false),
        new(CompatibilitySwitch, FirewallSection, CompatibilityScenarios, ExpandFirewallDetails: false,
            AllSizes: true),
        new(CorrelationSwitch, FirewallSection, CorrelationScenarios, ExpandFirewallDetails: true, AllSizes: false),
        new(ExternalReachabilitySwitch, ExternalSection, ExternalReachabilityScenarios,
            ExpandFirewallDetails: false, AllSizes: true),
        new(Switch, FirewallSection, Scenarios, ExpandFirewallDetails: false, AllSizes: true)
    ];

    public static bool TryRun(Application app, IReadOnlyList<string> arguments)
    {
        var mode = Modes.FirstOrDefault(candidate =>
            arguments.Contains(candidate.Switch, StringComparer.Ordinal));
        if (mode is null)
            return false;
        var index = arguments.ToList().FindIndex(value => value.Equals(mode.Switch, StringComparison.Ordinal));
        if (index < 0)
            return false;
        if (index + 1 >= arguments.Count)
            throw new ArgumentException($"{mode.Switch} requires an output directory.");

        var output = Path.GetFullPath(arguments[index + 1]);
        Directory.CreateDirectory(output);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            try
            {
                await RenderAsync(app, output, mode).ConfigureAwait(true);
                app.Shutdown(0);
            }
            catch (Exception exception)
            {
                File.WriteAllText(Path.Combine(output, "render-error.txt"), exception.ToString());
                app.Shutdown(1);
            }
        });
        return true;
    }

    private static async Task RenderAsync(Application app, string output, ReviewMode mode)
    {
        var viewModel = new MainViewModel(new NoAgentClient(), new NoDialogs());
        var page = new ServerOverviewPage();
        var scroll = new ScrollViewer
        {
            Content = page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(24)
        };
        var window = new Window
        {
            Title = "ChunkPilot firewall visual review",
            Content = scroll,
            DataContext = viewModel,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)app.FindResource("AppBackground")
        };
        AppTheme.Attach(window);
        window.Show();

        var serverId = Guid.Parse("8f17414b-dc5c-49e7-98df-22132fbeab1e");
        var java = @"D:\ChunkPilot\ManagedJava\temurin-21\bin\java.exe";
        var sizes = mode.AllSizes
            ?
            [
            ("800x600", 800, 600),
            ("1000x700", 1000, 700),
            ("1440x900", 1440, 900),
            ("maximized-1920x1080", 1920, 1080)
            ]
            : new (string Name, int Width, int Height)[] { ("1000x700", 1000, 700) };

        foreach (var scenario in mode.Scenarios(serverId, java))
        {
            foreach (var size in sizes)
            {
                window.Width = size.Width;
                window.Height = size.Height;
                viewModel.SetFirewallReviewState(
                    Server(serverId, scenario.Running ?? scenario.Router.Phase == RouterMappingPhase.Active),
                    scenario.Router,
                    scenario.Firewall,
                    scenario.Consent,
                    scenario.External);
                if (mode.ExpandFirewallDetails)
                    viewModel.ShowsFirewallTechnicalDetails = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                if (page.FindName(mode.Section) is FrameworkElement section)
                {
                    var point = section.TranslatePoint(new Point(0, 0), page);
                    scroll.ScrollToVerticalOffset(Math.Max(0, point.Y - 32));
                    window.UpdateLayout();
                }
                Save(window, Path.Combine(output, $"{scenario.Name}--{size.Name}.png"));
            }
        }
        window.Close();
    }

    /// <summary>
    /// Every external access state a person can actually land on, including the ones where nothing
    /// was concluded. Invented data only: no probe endpoint exists here and none is contacted.
    /// </summary>
    private static IReadOnlyList<ReviewScenario> ExternalReachabilityScenarios(Guid serverId, string java)
    {
        const string publicAddress = "203.0.113.7";
        var firewallReady = State(serverId, java, FirewallAccessPhase.Configured) with
        {
            Configured = true,
            Owner = FirewallRuleOwner.ChunkPilot,
            RuleId = Guid.Parse("1f372f14-f363-4b2c-b8d4-2bd7f51d4554"),
            RuleName = "ChunkPilot Minecraft server (1f372f14-f363-4b2c-b8d4-2bd7f51d4554)",
            ConfiguredAt = DateTimeOffset.UtcNow
        };
        var firewallNeedsPermission = State(serverId, java, FirewallAccessPhase.NeedsPermission);
        var routerActive = new RouterMappingState
        {
            ServerId = serverId,
            Phase = RouterMappingPhase.Active,
            Enabled = true,
            ConsentGranted = true,
            Mechanism = RouterMappingMechanism.UpnpIgd,
            Transport = MappingTransport.Tcp,
            InternalClient = "10.0.0.140",
            InternalPort = 25566,
            ExternalPort = 25566,
            GatewayAddress = "10.0.0.1",
            RouterReportedExternalAddress = publicAddress,
            RouterReportedAddressClass = RoutableAddressClass.GloballyRoutable,
            MappingInstanceId = "5b7d1c0e9a4f43b1a2c8d6e0f1234567",
            LeaseIsFinite = true,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            LastCheckedAt = DateTimeOffset.UtcNow
        };
        var routerInactive = routerActive with
        {
            Phase = RouterMappingPhase.Inactive,
            ExternalPort = 0,
            InternalClient = "",
            // Nothing is open, so there is no establishment to name.
            MappingInstanceId = ""
        };
        var routerShared = routerActive with
        {
            RouterReportedExternalAddress = "100.64.9.12",
            RouterReportedAddressClass = RoutableAddressClass.SharedAddressSpace,
            UpstreamNatSuspected = true
        };

        var endpoint = new ExternalReachabilityEndpoint
        {
            ServerId = serverId,
            PublicAddress = publicAddress,
            ExternalPort = 25566,
            InternalPort = 25566,
            MappingIdentity = "5b7d1c0e9a4f43b1a2c8d6e0f1234567:UpnpIgd/Tcp/10.0.0.140:25566->25566",
            RunIdentity = "18244@638900000000000000"
        };
        var checkedAt = new DateTimeOffset(2026, 8, 8, 19, 42, 0, TimeSpan.Zero);
        var ready = new ExternalReachabilityState
        {
            ServerId = serverId,
            Phase = ExternalReachabilityPhase.NotChecked,
            ProbeConfigured = true,
            Endpoint = endpoint,
            RouterReportedAddress = publicAddress,
            Port = 25566,
            LastOperationDetail = "No external check has run for this server."
        };
        var verified = ready with
        {
            Phase = ExternalReachabilityPhase.Reachable,
            CheckedEndpoint = endpoint,
            ObservedAddress = publicAddress,
            ConnectMilliseconds = 118,
            CheckedAt = checkedAt,
            LastOperationDetail = "External probe service answered 200 with result Reachable."
        };

        return
        [
            new("01-ready-to-check", firewallReady, routerActive, false, ready),
            new("02-checking", firewallReady, routerActive, false, ready with
            {
                Phase = ExternalReachabilityPhase.Checking,
                Busy = true,
                OperationId = Guid.Parse("6b0d1e2f-3a4b-4c5d-8e9f-0a1b2c3d4e5f")
            }),
            new("03-reachable", firewallReady, routerActive, false, verified),
            new("04-unreachable-router-and-firewall-configured", firewallReady, routerActive, false, ready with
            {
                Phase = ExternalReachabilityPhase.Unreachable,
                CheckedEndpoint = endpoint,
                ObservedAddress = publicAddress,
                CheckedAt = checkedAt,
                LastOperationDetail = "External probe service answered 200 with result Unreachable."
            }),
            new("05-source-address-mismatch", firewallReady, routerActive, false, ready with
            {
                Phase = ExternalReachabilityPhase.SourceAddressMismatch,
                CheckedEndpoint = endpoint,
                ObservedAddress = "198.51.100.42",
                CheckedAt = checkedAt,
                LastOperationDetail = "External probe service answered 200 with result SourceMismatch."
            }),
            new("06-probe-service-unavailable", firewallReady, routerActive, false, ready with
            {
                Phase = ExternalReachabilityPhase.ProbeUnavailable,
                CheckedEndpoint = endpoint,
                CheckedAt = checkedAt,
                LastOperationDetail = "The external probe service could not be reached."
            }),
            new("07-rate-limited", firewallReady, routerActive, false, ready with
            {
                Phase = ExternalReachabilityPhase.RateLimited,
                CheckedEndpoint = endpoint,
                CheckedAt = checkedAt,
                LastOperationDetail = "External probe service answered 429 with result RateLimited."
            }),
            new("08-server-stopped-router-inactive", firewallReady, routerInactive, false, new ExternalReachabilityState
            {
                ServerId = serverId,
                Phase = ExternalReachabilityPhase.Ineligible,
                Blocker = ExternalReachabilityBlocker.ServerNotRunning,
                ProbeConfigured = true,
                RouterReportedAddress = publicAddress,
                LastOperationDetail = "No external check has run for this server."
            }, Running: false),
            new("09-firewall-diagnostic-with-external-success", firewallNeedsPermission, routerActive, false, verified),
            new("10-invalidated-after-lifecycle-change", firewallReady, routerActive, false, ready with
            {
                Phase = ExternalReachabilityPhase.Stale,
                CheckedEndpoint = endpoint with { RunIdentity = "17120@638800000000000000" },
                ObservedAddress = publicAddress,
                CheckedAt = checkedAt,
                LastOperationDetail = "External probe service answered 200 with result Reachable."
            }),
            new("11-probe-not-available-in-this-build", firewallReady, routerActive, false, new ExternalReachabilityState
            {
                ServerId = serverId,
                Phase = ExternalReachabilityPhase.Ineligible,
                Blocker = ExternalReachabilityBlocker.ProbeNotConfigured,
                ProbeConfigured = false,
                Endpoint = endpoint,
                RouterReportedAddress = publicAddress,
                Port = 25566,
                LastOperationDetail = "No external probe endpoint is configured in this build."
            }),
            new("12-router-has-no-public-address", firewallReady, routerShared, false, new ExternalReachabilityState
            {
                ServerId = serverId,
                Phase = ExternalReachabilityPhase.Ineligible,
                Blocker = ExternalReachabilityBlocker.PublicAddressNotRoutable,
                ProbeConfigured = true,
                RouterReportedAddress = "100.64.9.12",
                Port = 25566,
                LastOperationDetail = "No external check has run for this server."
            })
        ];
    }

    private static IReadOnlyList<ReviewScenario> PolicyReadScenarios(Guid serverId, string java)
    {
        var publicState = State(serverId, java, FirewallAccessPhase.PublicNetworkConfirmationRequired) with
        {
            Profiles = FirewallProfile.Public,
            SelectedProfile = FirewallProfile.Public,
            Category = WindowsNetworkCategory.Public,
            NetworkName = "NootsBoots",
            FirewallPolicyDetail = "Windows Firewall policy read completed. Rules: read 631."
        };
        var router = new RouterMappingState { Phase = RouterMappingPhase.Inactive, Enabled = true };
        return
        [
            new("complete-public-policy", publicState, router, false),
            new("partial-policy-rules-unavailable", publicState with
            {
                Phase = FirewallAccessPhase.NeedsAttention,
                FirewallPolicyUnavailableFields = FirewallPolicyUnavailableFields.Rules,
                FirewallPolicyDetail = "Windows Firewall policy was partially read; unavailable: Rules. " +
                                       "Windows Firewall rules could not be enumerated."
            }, router, false),
            new("platform-unavailable", publicState with
            {
                Phase = FirewallAccessPhase.Unsupported,
                FirewallApiAvailable = false,
                FirewallPlatformStatus = FirewallPlatformStatus.ReadFailed,
                ModifyState = FirewallPolicyModifyState.Unknown,
                FirewallPolicyDetail = "The firewall policy component HNetCfg.FwPolicy2 could not be created " +
                                       "(0x80040154)."
            }, router, false)
        ];
    }

    private static IReadOnlyList<ReviewScenario> CompatibilityScenarios(Guid serverId, string java)
    {
        var basic = State(serverId, java, FirewallAccessPhase.NeedsPermission);
        var ready = basic with
        {
            Phase = FirewallAccessPhase.Configured,
            Configured = true,
            Owner = FirewallRuleOwner.ChunkPilot,
            RuleName = "ChunkPilot Minecraft server (1f372f14-f363-4b2c-b8d4-2bd7f51d4554)"
        };
        var router = new RouterMappingState { Phase = RouterMappingPhase.Inactive, Enabled = true };
        return
        [
            new("01-vpn-physical-selected", basic with
            {
                TargetDetail = "Ethernet owns the routed LAN endpoint; connected WinTUN and link-local adapters were excluded."
            }, router, false),
            new("02-group-policy-managed", basic with
            {
                Phase = FirewallAccessPhase.ManagedByOrganization,
                ModifyState = FirewallPolicyModifyState.GroupPolicyOverride
            }, router, false),
            new("03-firewall-disabled", basic with
            {
                Phase = FirewallAccessPhase.FirewallDisabled,
                FirewallEnabledForProfile = false
            }, router, false),
            new("04-firewall-platform-unavailable", basic with
            {
                Phase = FirewallAccessPhase.Unsupported,
                FirewallApiAvailable = false,
                FirewallPlatformStatus = FirewallPlatformStatus.ReadFailed,
                FirewallPolicyDetail = "FirewallAPI policy read failed (0x800706D9)."
            }, router, false),
            new("05-multiple-network-ambiguity", basic with
            {
                Phase = FirewallAccessPhase.NetworkUnavailable,
                Failure = FirewallAccessFailure.NetworkUnavailable,
                TargetProblem = FirewallTargetProblem.NetworkPathAmbiguous,
                NetworkPathStatus = NetworkPathStatus.Ambiguous,
                SelectedProfile = FirewallProfile.None
            }, router, false),
            new("06-network-profile-missing", basic with
            {
                Phase = FirewallAccessPhase.NetworkUnavailable,
                Failure = FirewallAccessFailure.NetworkUnavailable,
                TargetProblem = FirewallTargetProblem.NetworkProfileUnavailable,
                SelectedProfile = FirewallProfile.None,
                NetworkListDetail = "NLM returned no connected profile for InterfaceIndex 16."
            }, router, false),
            new("07-public-approval", basic with
            {
                Phase = FirewallAccessPhase.PublicNetworkConfirmationRequired,
                Category = WindowsNetworkCategory.Public,
                SelectedProfile = FirewallProfile.Public,
                Profiles = FirewallProfile.Public,
                NetworkName = "NootsBoots"
            }, router, false),
            new("08-uac-cancelled", basic with { Failure = FirewallAccessFailure.Cancelled }, router, false),
            new("09-elevation-denied", basic with
            {
                Phase = FirewallAccessPhase.NeedsAttention,
                Failure = FirewallAccessFailure.AccessDenied,
                LastOperationDetail = "Shell elevation returned access denied (0x80070005)."
            }, router, false),
            new("10-explicit-block", basic with
            {
                Phase = FirewallAccessPhase.BlockedByPolicy,
                BlockingRuleName = "Administrator block Minecraft"
            }, router, false),
            new("11-foreign-allow", basic with
            {
                Phase = FirewallAccessPhase.ExistingWindowsRule,
                Owner = FirewallRuleOwner.ExistingWindowsRule,
                ExistingRuleName = "Java(TM) Platform SE binary"
            }, router, false),
            new("12-java-unresolved", basic with
            {
                Phase = FirewallAccessPhase.RuntimeUnavailable,
                Failure = FirewallAccessFailure.RuntimeUnavailable,
                ProgramPath = ""
            }, router, false),
            new("13-port-unresolved", basic with
            {
                Phase = FirewallAccessPhase.PortUnavailable,
                Failure = FirewallAccessFailure.PortUnavailable,
                Port = 0
            }, router, false),
            new("14-stale-rule", ready with
            {
                Phase = FirewallAccessPhase.Stale,
                Port = 25567,
                StaleReasons = ["local port changed from 25566 to 25567"]
            }, router, false),
            new("15-ownership-conflict", basic with
            {
                Phase = FirewallAccessPhase.OwnershipConflict,
                Failure = FirewallAccessFailure.OwnershipConflict,
                RuleName = "ChunkPilot Minecraft server (collision)"
            }, router, false),
            new("16-unknown-failure", basic with
            {
                Phase = FirewallAccessPhase.NeedsAttention,
                Failure = FirewallAccessFailure.Unknown,
                LastOperationDetail = "Unexpected test-only evidence 0x80004005."
            }, router, false)
        ];
    }

    private static IReadOnlyList<ReviewScenario> CorrelationScenarios(Guid serverId, string java)
    {
        var privateState = State(serverId, java, FirewallAccessPhase.NeedsPermission);
        var publicState = privateState with
        {
            Phase = FirewallAccessPhase.PublicNetworkConfirmationRequired,
            Profiles = FirewallProfile.Public,
            SelectedProfile = FirewallProfile.Public,
            Category = WindowsNetworkCategory.Public,
            NetworkName = "NootsBoots",
            InterfaceName = "Ethernet"
        };
        var router = new RouterMappingState { Phase = RouterMappingPhase.Inactive, Enabled = true };
        return
        [
            new("public-profile-recognized", publicState, router, false),
            new("public-confirmation-required", publicState, router, true),
            new("private-profile-recognized", privateState, router, false),
            new("genuine-unmatched-profile", privateState with
            {
                Phase = FirewallAccessPhase.NetworkUnavailable,
                Profiles = FirewallProfile.None,
                SelectedProfile = FirewallProfile.None,
                Category = WindowsNetworkCategory.Unknown,
                NetworkName = "",
                InterfaceName = "",
                Failure = FirewallAccessFailure.NetworkUnavailable,
                LastOperationDetail = "No trustworthy routed LAN adapter matched one connected Windows network profile."
            }, router, false),
            new("java-target-failure-network-retained", publicState with
            {
                Phase = FirewallAccessPhase.RuntimeUnavailable,
                ProgramPath = "",
                RuntimeSource = "",
                Failure = FirewallAccessFailure.RuntimeUnavailable,
                LastOperationDetail = "The managed Java assignment could not be verified."
            }, router, false),
            new("profile-failure-port-runtime-retained", privateState with
            {
                Phase = FirewallAccessPhase.NetworkUnavailable,
                Profiles = FirewallProfile.None,
                SelectedProfile = FirewallProfile.None,
                Category = WindowsNetworkCategory.Unknown,
                NetworkName = "",
                InterfaceName = "Ethernet",
                Failure = FirewallAccessFailure.NetworkUnavailable,
                LastOperationDetail = "Ethernet could not be correlated with one connected Windows profile."
            }, router, false)
        ];
    }

    private static IReadOnlyList<ReviewScenario> ForeignCoverageScenarios(Guid serverId, string java)
    {
        var basic = State(serverId, java, FirewallAccessPhase.NeedsPermission);
        var publicTarget = basic with
        {
            Phase = FirewallAccessPhase.PublicNetworkConfirmationRequired,
            Category = WindowsNetworkCategory.Public,
            Profiles = FirewallProfile.Public,
            SelectedProfile = FirewallProfile.Public
        };
        var ready = basic with
        {
            Phase = FirewallAccessPhase.Configured,
            Owner = FirewallRuleOwner.ChunkPilot,
            Configured = true,
            RuleId = Guid.Parse("1f372f14-f363-4b2c-b8d4-2bd7f51d4554"),
            RuleName = "ChunkPilot Minecraft server (1f372f14-f363-4b2c-b8d4-2bd7f51d4554)",
            ConfiguredAt = DateTimeOffset.UtcNow
        };
        var router = new RouterMappingState { Phase = RouterMappingPhase.Inactive, Enabled = true };
        return
        [
            new("adobe-public-approval", publicTarget with
            {
                OtherAllowRuleName = "Adobe Native Client",
                OtherAllowRuleCoverage = FirewallRuleCoverage.UnknownOrUnsupported
            }, router, false),
            new("exact-foreign-equivalent", basic with
            {
                Phase = FirewallAccessPhase.ExistingWindowsRule,
                Owner = FirewallRuleOwner.ExistingWindowsRule,
                ExistingRuleName = "Administrator exact Java rule",
                ExistingRuleCoverage = FirewallRuleCoverage.ExactEquivalent
            }, router, false),
            new("owned-ready-plus-broad-foreign", ready with
            {
                OtherAllowRuleName = "Other application broad allow",
                OtherAllowRuleCoverage = FirewallRuleCoverage.BroadUnrestricted
            }, router, false),
            new("exact-foreign-block", basic with
            {
                Phase = FirewallAccessPhase.BlockedByPolicy,
                BlockingRuleName = "Administrator exact Minecraft block",
                BlockingRuleCoverage = FirewallRuleCoverage.ExactEquivalent
            }, router, false),
            new("unknown-constrained-allow", publicTarget with
            {
                OtherAllowRuleName = "Conditional allow",
                OtherAllowRuleCoverage = FirewallRuleCoverage.UnknownOrUnsupported
            }, router, false),
            new("unknown-potential-block", basic with
            {
                Phase = FirewallAccessPhase.NeedsAttention,
                BlockingRuleName = "Conditional block",
                BlockingRuleCoverage = FirewallRuleCoverage.UnknownOrUnsupported
            }, router, false)
        ];
    }

    private static IReadOnlyList<ReviewScenario> Scenarios(Guid serverId, string java)
    {
        var basic = State(serverId, java, FirewallAccessPhase.NeedsPermission);
        var ready = basic with
        {
            Phase = FirewallAccessPhase.Configured,
            Owner = FirewallRuleOwner.ChunkPilot,
            Configured = true,
            RuleId = Guid.Parse("1f372f14-f363-4b2c-b8d4-2bd7f51d4554"),
            RuleName = "ChunkPilot Minecraft server (1f372f14-f363-4b2c-b8d4-2bd7f51d4554)",
            ConfiguredAt = DateTimeOffset.UtcNow
        };
        var routerOff = new RouterMappingState { Phase = RouterMappingPhase.Inactive, Enabled = true };
        var routerActive = new RouterMappingState
        {
            Phase = RouterMappingPhase.Active,
            Enabled = true,
            InternalPort = 25566,
            ExternalPort = 25566,
            InternalClient = "192.168.1.50",
            Mechanism = RouterMappingMechanism.UpnpIgd
        };
        return
        [
            new("needs-permission", basic, routerOff, false),
            new("confirmation", basic, routerOff, true),
            new("uac-pending", basic with { Phase = FirewallAccessPhase.WaitingForElevation, Busy = true }, routerOff, false),
            new("rule-ready", ready, routerOff, false),
            new("existing-foreign-allow", basic with { Phase = FirewallAccessPhase.ExistingWindowsRule, Owner = FirewallRuleOwner.ExistingWindowsRule, ExistingRuleName = "Java(TM) Platform SE binary" }, routerOff, false),
            new("public-network-warning", basic with { Phase = FirewallAccessPhase.PublicNetworkConfirmationRequired, Category = WindowsNetworkCategory.Public, Profiles = FirewallProfile.Public, SelectedProfile = FirewallProfile.Public }, routerOff, false),
            new("organization-managed", basic with { Phase = FirewallAccessPhase.ManagedByOrganization, ModifyState = FirewallPolicyModifyState.GroupPolicyOverride }, routerOff, false),
            new("block-conflict", basic with { Phase = FirewallAccessPhase.BlockedByPolicy, BlockingRuleName = "Administrator block Minecraft" }, routerOff, false),
            new("firewall-disabled", basic with { Phase = FirewallAccessPhase.FirewallDisabled, FirewallEnabledForProfile = false }, routerOff, false),
            new("stale-port", ready with { Phase = FirewallAccessPhase.Stale, StaleReasons = ["the local port is not 25567"], Port = 25567 }, routerOff, false),
            new("removal-failure", ready with { Phase = FirewallAccessPhase.NeedsAttention, RemovalPending = true, Failure = FirewallAccessFailure.RemovalFailed }, routerOff, false),
            new("router-active-firewall-incomplete", basic, routerActive, false),
            new("router-active-firewall-ready", ready, routerActive, false),
            new("server-stopped-router-inactive-firewall-ready", ready, routerOff, false)
        ];
    }

    private static WindowsFirewallState State(Guid serverId, string java, FirewallAccessPhase phase) => new()
    {
        ServerId = serverId,
        Phase = phase,
        ProgramPath = java,
        JavaVerified = true,
        RuntimeSource = "ChunkPilot managed Java runtime",
        Port = 25566,
        PortVerified = true,
        Transport = MappingTransport.Tcp,
        Profiles = FirewallProfile.Private,
        SelectedProfile = FirewallProfile.Private,
        NetworkProfileVerified = true,
        Category = WindowsNetworkCategory.Private,
        NetworkName = "Home network",
        InterfaceName = "Ethernet",
        InterfaceIndex = 16,
        LocalAddress = "10.0.0.140",
        GatewayAddress = "10.0.0.1",
        NetworkPathStatus = NetworkPathStatus.Available,
        NetworkListStatus = NetworkListStatus.Available,
        FirewallApiAvailable = true,
        FirewallPlatformStatus = FirewallPlatformStatus.Available,
        FirewallEnabledForProfile = true,
        ModifyState = FirewallPolicyModifyState.Ok,
        LastCheckedAt = DateTimeOffset.UtcNow,
        TargetDetail = "Ethernet matched the trusted routed LAN endpoint.",
        NetworkListDetail = "Network List Manager returned one exact InterfaceIndex match.",
        FirewallPolicyDetail = "Windows Firewall policy and relevant rules were read successfully."
    };

    private static ServerSnapshot Server(Guid id, bool running) => new()
    {
        Definition = new ServerDefinition
        {
            Id = id,
            Name = "Friends survival",
            RootPath = @"D:\ChunkPilot\Servers\Friends-survival",
            Executable = @"D:\ChunkPilot\ManagedJava\temurin-21\bin\java.exe",
            Port = 25566,
            MaximumRamMb = 4096,
            Ecosystem = ServerEcosystem.Vanilla
        },
        State = running ? ServerState.Running : ServerState.Stopped,
        MaxPlayers = 20,
        OnlinePlayers = running ? 3 : 0
    };

    private static void Save(Visual visual, string path)
    {
        if (visual is not FrameworkElement element)
            throw new InvalidOperationException("Review visual did not have a render size.");
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed record ReviewMode(
        string Switch,
        string Section,
        Func<Guid, string, IReadOnlyList<ReviewScenario>> Scenarios,
        bool ExpandFirewallDetails,
        bool AllSizes);

    /// <summary>
    /// <paramref name="Running"/> overrides the default of inferring the lifecycle from the router
    /// phase, which external access scenarios need: a stopped server and an inactive mapping are two
    /// separate facts there.
    /// </summary>
    private sealed record ReviewScenario(
        string Name,
        WindowsFirewallState Firewall,
        RouterMappingState Router,
        bool Consent,
        ExternalReachabilityState? External = null,
        bool? Running = null);

    private sealed class NoAgentClient : IAgentClient
    {
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TResponse> SendAsync<TResponse>(string operation, object? payload = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TResponse>(new InvalidOperationException("Visual review has no Agent."));
    }

    private sealed class NoDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }
}
