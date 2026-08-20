using System.Globalization;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App;

/// <summary>
/// Direct internet: the beginner-facing half of router port mapping.
/// </summary>
/// <remarks>
/// <para>
/// This partial holds presentation and deliberate user intent only. It contains no protocol knowledge:
/// UPnP, PCP and NAT-PMP live in Infrastructure, and every decision about what may be created, renewed
/// or removed on a router belongs to the Agent. The ViewModel asks and renders what comes back.
/// </para>
/// <para>
/// Nothing here starts a router operation on its own. Selecting a server, opening Overview and
/// refreshing all read state; only <see cref="ConfirmDirectInternetCommand"/> — reached through an
/// explicit confirmation the user must first open and then accept — ever changes a router.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>The connection methods a beginner chooses between. Only Direct internet is expanded.</summary>
    public IReadOnlyList<ConnectionMethodOption> ConnectionMethods { get; } =
    [
        new(NetworkMode.ThisComputerOnly, "This computer", "Only you, on this PC."),
        new(NetworkMode.HomeNetwork, "Home network", "People on your Wi-Fi or wired network."),
        new(NetworkMode.PortForwarding, "Direct internet", "Friends elsewhere, through your router."),
        new(NetworkMode.ConfigureLater, "Decide later", "Keep the server local for now.")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectInternetTitle))]
    [NotifyPropertyChangedFor(nameof(DirectInternetSummary))]
    [NotifyPropertyChangedFor(nameof(DirectInternetTone))]
    [NotifyPropertyChangedFor(nameof(DirectInternetBadge))]
    [NotifyPropertyChangedFor(nameof(DirectInternetIcon))]
    [NotifyPropertyChangedFor(nameof(DirectInternetPrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(ShowsDirectInternetPrimaryAction))]
    [NotifyPropertyChangedFor(nameof(ShowsDirectInternetRetry))]
    [NotifyPropertyChangedFor(nameof(ShowsDirectInternetTurnOff))]
    [NotifyPropertyChangedFor(nameof(ShowsDirectInternetCancel))]
    [NotifyPropertyChangedFor(nameof(ShowsRouterReportedAddress))]
    [NotifyPropertyChangedFor(nameof(RouterReportedAddressCaveat))]
    [NotifyPropertyChangedFor(nameof(RouterReportedEndpoint))]
    [NotifyPropertyChangedFor(nameof(ShowsUpstreamNetworkNotice))]
    [NotifyPropertyChangedFor(nameof(DirectInternetMechanismLabel))]
    [NotifyPropertyChangedFor(nameof(DirectInternetTransportLabel))]
    [NotifyPropertyChangedFor(nameof(DirectInternetLeaseLabel))]
    [NotifyPropertyChangedFor(nameof(DirectInternetGateway))]
    [NotifyPropertyChangedFor(nameof(DirectInternetInternalEndpoint))]
    [NotifyPropertyChangedFor(nameof(DirectInternetExternalPortLabel))]
    [NotifyPropertyChangedFor(nameof(DirectInternetLastCheckedLabel))]
    [NotifyPropertyChangedFor(nameof(DirectInternetAddressClassLabel))]
    [NotifyPropertyChangedFor(nameof(DirectInternetTechnicalDetail))]
    [NotifyPropertyChangedFor(nameof(IsDirectInternetBusy))]
    [NotifyPropertyChangedFor(nameof(FirewallCombinedStatus))]
    // External access composes the router state into its own diagnosis, so it has to be re-read
    // whenever the router state changes underneath it.
    [NotifyPropertyChangedFor(nameof(ExternalReachabilitySummary))]
    [NotifyPropertyChangedFor(nameof(ExternalReachabilityUpstreamAssessment))]
    [NotifyPropertyChangedFor(nameof(ShowsExternalReachabilityUpstreamAssessment))]
    private RouterMappingState routerMapping = new();

    /// <summary>True only while the inline confirmation is open. Never persisted, never remembered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDirectInternetPrimaryAction))]
    private bool showsDirectInternetConsent;

    [ObservableProperty]
    private IReadOnlyList<string> directInternetConsentPoints = [];

    /// <summary>Progressive disclosure for expert information. Collapsed by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectInternetTechnicalDetailsToggleText))]
    private bool showsDirectInternetTechnicalDetails;

    public string DirectInternetTechnicalDetailsToggleText =>
        ShowsDirectInternetTechnicalDetails ? "Hide technical details" : "Technical details";

    public bool IsDirectInternetSelected => SelectedNetworkMode == NetworkMode.PortForwarding;

    /// <summary>
    /// Whether the Agent's router-mapping state is worth re-reading right now.
    /// </summary>
    /// <remarks>
    /// The Agent changes this state on its own — renewing a lease, withdrawing an exposure once a
    /// server settles, recording a cleanup that failed — so the card cannot stay truthful on command
    /// results alone. It is only rendered for Direct internet, so that is the only time it is polled.
    /// </remarks>
    private bool ShouldPollRouterMapping => SelectedServer is not null && IsDirectInternetSelected;

    public string DirectInternetTitle => DirectInternetPresentation.Title(RouterMapping);
    public string DirectInternetSummary => DirectInternetPresentation.Summary(RouterMapping);
    public AppTone DirectInternetTone => DirectInternetPresentation.Tone(RouterMapping.Phase);
    public string DirectInternetBadge => DirectInternetPresentation.Badge(RouterMapping.Phase);
    public AppIconKind DirectInternetIcon => DirectInternetPresentation.Icon(RouterMapping.Phase);

    public string DirectInternetPrimaryActionText =>
        DirectInternetPresentation.PrimaryActionText(RouterMapping);

    public bool IsDirectInternetBusy => RouterMapping.Phase
        is RouterMappingPhase.Checking or RouterMappingPhase.Creating
        or RouterMappingPhase.Removing or RouterMappingPhase.Reconciling;

    /// <summary>
    /// Setting up is offered until there is something set up. A server that is configured and simply
    /// waiting to start has nothing left to set up, and a failed cleanup needs a retry rather than
    /// another capability check.
    /// </summary>
    public bool ShowsDirectInternetPrimaryAction =>
        !ShowsDirectInternetConsent && !IsDirectInternetBusy && !RouterMapping.RemovalPending &&
        RouterMapping.Phase is not RouterMappingPhase.Active and not RouterMappingPhase.Inactive;

    /// <summary>Offered only when a cleanup that ChunkPilot owes the router has not completed.</summary>
    public bool ShowsDirectInternetRetry => !IsDirectInternetBusy && RouterMapping.RemovalPending;

    public bool ShowsDirectInternetTurnOff =>
        !IsDirectInternetBusy && (RouterMapping.Enabled || RouterMapping.Phase == RouterMappingPhase.Active);

    public bool ShowsDirectInternetCancel => IsDirectInternetBusy;

    public bool ShowsRouterReportedAddress => RouterMapping.HasRouterReportedAddress;

    public string RouterReportedAddressLabel => DirectInternetPresentation.AddressLabel();
    public string RouterReportedAddressCaveat => DirectInternetPresentation.AddressCaveat(RouterMapping);
    public string RouterReportedEndpoint => RouterMapping.RouterReportedEndpoint;

    public bool ShowsUpstreamNetworkNotice => RouterMapping.UpstreamNatSuspected;

    public string UpstreamNetworkNotice =>
        "Your router appears to be behind another network layer, so opening a port on it may not be enough for " +
        "friends to reach you.";

    // ── Technical details. Protocol wording lives here and nowhere else. ──

    public string DirectInternetMechanismLabel =>
        DirectInternetPresentation.MechanismLabel(RouterMapping);

    public string DirectInternetTransportLabel =>
        DirectInternetPresentation.TransportLabel(RouterMapping.Transport);

    public string DirectInternetLeaseLabel => DirectInternetPresentation.LeaseLabel(RouterMapping);

    public string DirectInternetGateway =>
        RouterMapping.GatewayAddress.Length > 0 ? RouterMapping.GatewayAddress : "Not identified";

    /// <summary>
    /// The address a mapping forwards to, or the one it would forward to. The candidate is labelled as
    /// such: it answers "did ChunkPilot find a safe local address?" without implying a mapping exists.
    /// </summary>
    public string DirectInternetInternalEndpoint =>
        RouterMapping.InternalClient.Length > 0 && RouterMapping.InternalPort > 0
            ? $"{RouterMapping.InternalClient}:{RouterMapping.InternalPort}"
            : RouterMapping.CandidateInternalClient.Length > 0 && RouterMapping.InternalPort > 0
                ? $"{RouterMapping.CandidateInternalClient}:{RouterMapping.InternalPort} (candidate, not mapped)"
                : "Not established";

    public string DirectInternetExternalPortLabel =>
        RouterMapping.ExternalPort > 0 ? RouterMapping.ExternalPort.ToString(CultureInfo.InvariantCulture) : "—";

    public string DirectInternetLastCheckedLabel => RouterMapping.LastCheckedAt is { } checkedAt
        ? checkedAt.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture)
        : "Never";

    public string DirectInternetAddressClassLabel =>
        DirectInternetPresentation.AddressClassLabel(RouterMapping.RouterReportedAddressClass);

    public string DirectInternetTechnicalDetail => RouterMapping.LastOperationDetail.Length > 0
        ? RouterMapping.LastOperationDetail
        : "No router operation has run for this server.";

    /// <summary>
    /// Step one: ask the router what it can do. Read-only — this never creates an exposure, which is why
    /// it can be the button a beginner presses first.
    /// </summary>
    [RelayCommand]
    private async Task CheckDirectInternetAsync()
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        ClaimRouterMappingUpdate();
        ShowsDirectInternetConsent = false;
        await RunBusyAsync("Checking your router…", async () =>
        {
            RouterMapping = TrackPublicConnectivity(await client.SendAsync<RouterMappingState>(
                "CheckRouterMapping",
                ConnectivityRequest(serverId, PublicConnectivityOperation.CheckRouterCapability))
                .ConfigureAwait(true));
            if (RouterMapping.Phase == RouterMappingPhase.Supported && !RouterMapping.Enabled)
            {
                // The router can do it. Nothing has changed yet: opening the confirmation is the only
                // thing this success leads to.
                DirectInternetConsentPoints =
                    DirectInternetPresentation.ConsentPoints(RouterMapping.InternalPort);
                ShowsDirectInternetConsent = true;
            }
            StatusMessage = DirectInternetPresentation.Title(RouterMapping);
        }, failureAction: "Router check failed").ConfigureAwait(true);
    }

    /// <summary>
    /// Step two, and the only path to a router change: the user has read the confirmation and accepted it
    /// for this server.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmDirectInternetAsync()
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        ClaimRouterMappingUpdate();
        ShowsDirectInternetConsent = false;
        await RunBusyAsync("Asking your router to open the port…", async () =>
        {
            RouterMapping = TrackPublicConnectivity(await client.SendAsync<RouterMappingState>(
                "EnableRouterMapping", new EnableRouterMappingRequest(serverId, true)
                {
                    Session = uiSession,
                    ExpectedLease = LeaseFor(serverId),
                    ConnectivityOperation = PublicConnectivityOperation.EnableRouterMapping
                }).ConfigureAwait(true));
            StatusMessage = DirectInternetPresentation.Title(RouterMapping);
        }, failureAction: "Router setup failed").ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelDirectInternetConsent() => ShowsDirectInternetConsent = false;

    [RelayCommand]
    private void ToggleDirectInternetTechnicalDetails() =>
        ShowsDirectInternetTechnicalDetails = !ShowsDirectInternetTechnicalDetails;

    [RelayCommand]
    private async Task TurnOffDirectInternetAsync()
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        ClaimRouterMappingUpdate();
        ShowsDirectInternetConsent = false;
        await RunBusyAsync("Undoing the router setup…", async () =>
        {
            RouterMapping = TrackPublicConnectivity(await client.SendAsync<RouterMappingState>(
                "DisableRouterMapping",
                ConnectivityRequest(serverId, PublicConnectivityOperation.DisableRouterMapping))
                .ConfigureAwait(true));
            StatusMessage = DirectInternetPresentation.Title(RouterMapping);
        }, failureAction: "Turning off Direct internet failed").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CancelDirectInternetOperationAsync()
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        RouterMapping = TrackPublicConnectivity(await client.SendAsync<RouterMappingState>(
            "CancelRouterMapping",
            ConnectivityRequest(serverId, PublicConnectivityOperation.CancelRouterMapping))
            .ConfigureAwait(true));
        StatusMessage = "Router setup cancelled.";
    }

    [RelayCommand]
    private void CopyRouterReportedAddress()
    {
        if (!RouterMapping.HasRouterReportedAddress)
            return;
        System.Windows.Clipboard.SetText(RouterMapping.RouterReportedEndpoint);
        StatusMessage = "Router-reported address copied. ChunkPilot has not verified it from outside your network.";
    }

    [RelayCommand]
    private void CopyDirectInternetTechnicalDetails()
    {
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine,
            $"Mechanism: {DirectInternetMechanismLabel}",
            $"Transport: {DirectInternetTransportLabel}",
            $"Gateway: {DirectInternetGateway}",
            $"Internal endpoint: {DirectInternetInternalEndpoint}",
            $"External port: {DirectInternetExternalPortLabel}",
            $"Lease: {DirectInternetLeaseLabel}",
            $"Last checked: {DirectInternetLastCheckedLabel}",
            $"Router-reported address: {(RouterMapping.HasRouterReportedAddress ? RouterMapping.RouterReportedExternalAddress : "Not reported")}",
            $"Address classification: {DirectInternetAddressClassLabel}",
            $"Last operation: {DirectInternetTechnicalDetail}"));
        StatusMessage = "Technical details copied.";
    }

    /// <summary>
    /// Retries the cleanup ChunkPilot still owes the router. Runs the same reconciliation the Agent
    /// performs on its own; it never creates an exposure.
    /// </summary>
    [RelayCommand]
    private async Task RetryDirectInternetCleanupAsync()
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        ClaimRouterMappingUpdate();
        await RunBusyAsync("Closing the router port…", async () =>
        {
            RouterMapping = TrackPublicConnectivity(await client.SendAsync<RouterMappingState>(
                "RetryRouterMapping",
                ConnectivityRequest(serverId, PublicConnectivityOperation.RetryRouterMapping))
                .ConfigureAwait(true));
            StatusMessage = DirectInternetPresentation.Title(RouterMapping);
        }, failureAction: "Closing the router port failed").ConfigureAwait(true);
    }

    /// <summary>
    /// Marks every router-mapping read that is already in flight as superseded.
    /// </summary>
    /// <remarks>
    /// The Agent is authoritative, but its answers can still arrive out of order: a background refresh
    /// started before a Turn off can land after it and put the old, active-looking state back on screen.
    /// Commands claim the slot before they call, so a slower earlier read is discarded instead of
    /// resurrecting a mapping that no longer exists.
    /// </remarks>
    private void ClaimRouterMappingUpdate() => Interlocked.Increment(ref routerMappingSequence);

    private int routerMappingSequence;

    /// <summary>Reads the authoritative state. Never contacts the router and never creates anything.</summary>
    private async Task LoadRouterMappingAsync()
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        var sequence = Volatile.Read(ref routerMappingSequence);
        var state = TrackPublicConnectivity(await client.SendAsync<RouterMappingState>(
            "GetRouterMapping",
            ConnectivityRequest(serverId, PublicConnectivityOperation.ReadRouterState))
            .ConfigureAwait(true));
        if (Volatile.Read(ref routerMappingSequence) != sequence ||
            SelectedServer?.Definition.Id != serverId)
            return;
        RouterMapping = state;
    }
}

/// <summary>One beginner-facing connection choice.</summary>
public sealed record ConnectionMethodOption(NetworkMode Mode, string Title, string Description);
