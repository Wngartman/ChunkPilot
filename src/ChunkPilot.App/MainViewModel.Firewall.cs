using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.Presentation;
using ChunkPilot.App.Navigation;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App;

/// <summary>Consent, UAC initiation, and presentation for one server's Windows Firewall access.</summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirewallTitle))]
    [NotifyPropertyChangedFor(nameof(FirewallSummary))]
    [NotifyPropertyChangedFor(nameof(FirewallTone))]
    [NotifyPropertyChangedFor(nameof(FirewallBadge))]
    [NotifyPropertyChangedFor(nameof(FirewallDiagnostic))]
    [NotifyPropertyChangedFor(nameof(FirewallPrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(FirewallSecondaryActionText))]
    [NotifyPropertyChangedFor(nameof(ShowsFirewallPrimaryAction))]
    [NotifyPropertyChangedFor(nameof(ShowsFirewallSecondaryAction))]
    [NotifyPropertyChangedFor(nameof(ShowsFirewallTechnicalDetailsToggle))]
    [NotifyPropertyChangedFor(nameof(ShowsFirewallRemoveAction))]
    [NotifyPropertyChangedFor(nameof(ShowsFirewallCancelAction))]
    [NotifyPropertyChangedFor(nameof(FirewallCombinedStatus))]
    [NotifyPropertyChangedFor(nameof(FirewallTechnicalDetail))]
    [NotifyPropertyChangedFor(nameof(FirewallRequiresPublicApproval))]
    [NotifyPropertyChangedFor(nameof(FirewallConsentTitle))]
    [NotifyPropertyChangedFor(nameof(FirewallConsentMessage))]
    [NotifyPropertyChangedFor(nameof(FirewallNetworkDisplay))]
    [NotifyPropertyChangedFor(nameof(FirewallPortDisplay))]
    [NotifyPropertyChangedFor(nameof(FirewallJavaDisplay))]
    [NotifyPropertyChangedFor(nameof(FirewallInterfaceIndexDisplay))]
    [NotifyPropertyChangedFor(nameof(FirewallProfileDisplay))]
    [NotifyPropertyChangedFor(nameof(FirewallEnabledDisplay))]
    [NotifyPropertyChangedFor(nameof(FirewallLastCheckedDisplay))]
    // The external diagnosis is composed from the firewall state, never a copy of it.
    [NotifyPropertyChangedFor(nameof(ExternalReachabilitySummary))]
    private WindowsFirewallState firewallAccess = new();

    [ObservableProperty]
    private bool showsFirewallConsent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirewallTechnicalDetailsToggleText))]
    [NotifyPropertyChangedFor(nameof(ShowsFirewallTechnicalDetailsToggle))]
    private bool showsFirewallTechnicalDetails;

    public string FirewallTitle => WindowsFirewallPresentation.Title(FirewallAccess);
    public string FirewallSummary => WindowsFirewallPresentation.Summary(FirewallAccess);
    public AppTone FirewallTone => WindowsFirewallPresentation.Tone(FirewallAccess);
    public string FirewallBadge => WindowsFirewallPresentation.Badge(FirewallAccess);
    public FirewallDiagnostic FirewallDiagnostic => WindowsFirewallPresentation.Diagnostic(FirewallAccess);
    public string FirewallPrimaryActionText =>
        WindowsFirewallPresentation.ActionText(FirewallDiagnostic.PrimaryAction);
    public string FirewallSecondaryActionText =>
        WindowsFirewallPresentation.ActionText(FirewallDiagnostic.SecondaryAction);
    public bool ShowsFirewallPrimaryAction => !ShowsFirewallConsent && !FirewallAccess.Busy &&
        FirewallDiagnostic.PrimaryAction != FirewallRecoveryAction.None;
    public bool ShowsFirewallSecondaryAction => !ShowsFirewallConsent && !FirewallAccess.Busy &&
        FirewallDiagnostic.SecondaryAction != FirewallRecoveryAction.None &&
        FirewallDiagnostic.SecondaryAction != FirewallDiagnostic.PrimaryAction;
    public bool ShowsFirewallTechnicalDetailsToggle => ShowsFirewallTechnicalDetails ||
        FirewallDiagnostic.PrimaryAction != FirewallRecoveryAction.ViewDetails;
    public bool ShowsFirewallRemoveAction =>
        !FirewallAccess.Busy && FirewallAccess.Configured && !FirewallAccess.RemovalPending;
    public bool ShowsFirewallCancelAction => FirewallAccess.Busy;
    public bool FirewallRequiresPublicApproval =>
        FirewallAccess.Phase == FirewallAccessPhase.PublicNetworkConfirmationRequired;
    public string FirewallConsentTitle => FirewallRequiresPublicApproval
        ? "Allow this server on a Public network?"
        : FirewallAccess.RemovalPending
            ? "Retry removing this firewall rule?"
        : FirewallAccess.Phase == FirewallAccessPhase.Stale
            ? "Update this server's firewall rule?"
            : "Allow this server through Windows Firewall?";
    public string FirewallConsentMessage => FirewallRequiresPublicApproval
        ? $"Windows considers {(FirewallAccess.InterfaceName.Length > 0 ? FirewallAccess.InterfaceName : "this network")} a Public network. " +
          "ChunkPilot will not change that category. Continuing creates one inbound TCP rule for this server's exact Java executable, port, and Public profile."
        : FirewallAccess.RemovalPending
            ? "Windows will show an administrator prompt. ChunkPilot will retry removing only the firewall rule it can prove it created, then verify that the rule is gone."
        : "Windows will show an administrator prompt. ChunkPilot will create or update one inbound TCP rule for this server's exact Java executable, port, and current network profile.";
    public string FirewallCombinedStatus =>
        WindowsFirewallPresentation.CombinedStatus(RouterMapping, FirewallAccess, ExternalReachability);
    public string FirewallTechnicalDetailsToggleText =>
        ShowsFirewallTechnicalDetails ? "Hide firewall details" : "Firewall technical details";
    public string FirewallTechnicalDetail => FirewallAccess.LastOperationDetail.Length > 0
        ? FirewallAccess.LastOperationDetail
        : "No firewall mutation result is recorded for this snapshot.";
    public string FirewallNetworkDisplay => FirewallAccess.InterfaceName.Length > 0
        ? FirewallAccess.InterfaceName
        : FirewallAccess.NetworkName;
    public string FirewallPortDisplay => FirewallAccess.PortVerified && FirewallAccess.Port > 0
        ? $"TCP {FirewallAccess.Port}"
        : "Not verified";
    public string FirewallJavaDisplay => FirewallAccess.JavaVerified && FirewallAccess.ProgramPath.Length > 0
        ? FirewallAccess.ProgramPath
        : "Not verified";
    public string FirewallInterfaceIndexDisplay => FirewallAccess.InterfaceIndex > 0
        ? FirewallAccess.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "Not available";
    public string FirewallProfileDisplay => !FirewallAccess.NetworkProfileVerified ||
        FirewallAccess.SelectedProfile == FirewallProfile.None
        ? "Not available"
        : WindowsFirewallPolicy.DescribeProfiles(FirewallAccess.SelectedProfile);
    public string FirewallEnabledDisplay => !FirewallAccess.FirewallApiAvailable ||
        FirewallAccess.FirewallPolicyUnavailableFields.HasFlag(FirewallPolicyUnavailableFields.FirewallEnabled)
        ? "Unknown"
        : FirewallAccess.FirewallEnabledForProfile ? "Enabled" : "Disabled";
    public string FirewallLastCheckedDisplay => FirewallAccess.LastCheckedAt?.ToLocalTime()
        .ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "Not checked";

    [RelayCommand]
    private async Task CheckFirewallAccessAsync()
    {
        if (SelectedServer is null)
            return;
        ShowsFirewallConsent = false;
        FirewallAccess = await client.SendAsync<WindowsFirewallState>(
            "CheckFirewallAccess", new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true);
        StatusMessage = FirewallTitle;
    }

    [RelayCommand]
    private async Task ExecuteFirewallPrimaryActionAsync() =>
        await ExecuteFirewallActionAsync(FirewallDiagnostic.PrimaryAction).ConfigureAwait(true);

    [RelayCommand]
    private async Task ExecuteFirewallSecondaryActionAsync() =>
        await ExecuteFirewallActionAsync(FirewallDiagnostic.SecondaryAction).ConfigureAwait(true);

    private async Task ExecuteFirewallActionAsync(FirewallRecoveryAction action)
    {
        switch (action)
        {
            case FirewallRecoveryAction.CheckAgain:
                await CheckFirewallAccessAsync().ConfigureAwait(true);
                break;
            case FirewallRecoveryAction.Configure:
            case FirewallRecoveryAction.ConfirmPublic:
            case FirewallRecoveryAction.Update:
            case FirewallRecoveryAction.RetryRemoval:
            case FirewallRecoveryAction.TryAgain:
                ShowsFirewallConsent = true;
                break;
            case FirewallRecoveryAction.ReviewJava:
                NavigateServerDestination(ServerDestination.Settings);
                break;
            case FirewallRecoveryAction.ReviewPort:
                NavigateServerDestination(ServerDestination.Manage);
                break;
            case FirewallRecoveryAction.ViewDetails:
                ShowsFirewallTechnicalDetails = true;
                break;
        }
    }

    [RelayCommand]
    private void CopyFirewallTechnicalDetails()
    {
        var server = FirewallAccess.ServerId == Guid.Empty
            ? "unknown"
            : FirewallAccess.ServerId.ToString("N")[..8];
        var lines = new[]
        {
            $"ChunkPilot firewall diagnostic: {FirewallDiagnostic.Reason}",
            $"Server: {server}",
            $"Phase: {FirewallAccess.Phase}",
            $"Target: {FirewallPortDisplay}; Java={Value(FirewallAccess.ProgramPath)}",
            $"Network: Adapter={Value(FirewallAccess.InterfaceName)}; Index={FirewallInterfaceIndexDisplay}; Local={Value(FirewallAccess.LocalAddress)}; Gateway={Value(FirewallAccess.GatewayAddress)}; Profile={FirewallProfileDisplay}",
            $"Firewall: Platform={FirewallAccess.FirewallPlatformStatus}; Unavailable={FirewallAccess.FirewallPolicyUnavailableFields}; Enabled={FirewallEnabledDisplay}; BlockAllInbound={FirewallAccess.BlockAllInboundForProfile}; Modify={FirewallAccess.ModifyState}; Owner={FirewallAccess.Owner}",
            $"Rules: Owned={Value(FirewallAccess.RuleName)}; ExactForeign={Value(FirewallAccess.ExistingRuleName)}; ExactCoverage={FirewallAccess.ExistingRuleCoverage}; OtherAllow={Value(FirewallAccess.OtherAllowRuleName)}; OtherCoverage={FirewallAccess.OtherAllowRuleCoverage}; Blocking={Value(FirewallAccess.BlockingRuleName)}; BlockingCoverage={FirewallAccess.BlockingRuleCoverage}",
            $"Last checked: {FirewallLastCheckedDisplay}",
            $"Target evidence: {Value(FirewallAccess.TargetDetail)}",
            $"Network List evidence: {Value(FirewallAccess.NetworkListDetail)}",
            $"Firewall evidence: {Value(FirewallAccess.FirewallPolicyDetail)}",
            $"Operation: {Value(FirewallAccess.LastOperationDetail)}"
        };
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
        StatusMessage = "Firewall technical details copied.";
    }

    private static string Value(string value) => value.Length == 0 ? "not available" : value;

    [RelayCommand]
    private void RequestFirewallAccess() => ShowsFirewallConsent = true;

    [RelayCommand]
    private void CancelFirewallConsent() => ShowsFirewallConsent = false;

    [RelayCommand]
    private async Task ConfirmFirewallAccessAsync()
    {
        await RunFirewallMutationAsync(
            FirewallAccess.RemovalPending ? FirewallHelperOperation.Remove : FirewallHelperOperation.Create,
            FirewallRequiresPublicApproval).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveFirewallAccessAsync()
    {
        if (SelectedServer is null)
            return;
        if (!dialogs.Confirm("Remove Windows Firewall access",
                "Remove only the Windows Firewall rule ChunkPilot can prove it created for this server? The server folder and router settings are unchanged."))
            return;
        await RunFirewallMutationAsync(FirewallHelperOperation.Remove, publicApproved: false)
            .ConfigureAwait(true);
    }

    internal Task RemoveFirewallAccessFromWebUiAsync() =>
        RunFirewallMutationAsync(FirewallHelperOperation.Remove, publicApproved: false);

    [RelayCommand]
    private async Task CancelFirewallOperationAsync()
    {
        if (SelectedServer is null)
            return;
        FirewallAccess = await client.SendAsync<WindowsFirewallState>(
            "CancelFirewallAccess",
            ConnectivityRequest(SelectedServer.Definition.Id,
                PublicConnectivityOperation.CancelFirewallAccess)).ConfigureAwait(true);
        StatusMessage = "Firewall operation cancelled.";
    }

    [RelayCommand]
    private void ToggleFirewallTechnicalDetails() =>
        ShowsFirewallTechnicalDetails = !ShowsFirewallTechnicalDetails;

    private async Task RunFirewallMutationAsync(FirewallHelperOperation requested, bool publicApproved)
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        ShowsFirewallConsent = false;
        var ticket = await client.SendAsync<FirewallElevationTicket>(
            "PrepareFirewallAccess",
            new PrepareFirewallAccessRequest(serverId, requested, publicApproved)
            {
                Session = uiSession,
                ConnectivityOperation = PublicConnectivityOperation.PrepareFirewallAccess
            }).ConfigureAwait(true);
        FirewallAccess = ticket.State;
        if (!ticket.Ready)
        {
            StatusMessage = ticket.Detail;
            return;
        }

        var launcher = firewallLauncherFactory();
        FirewallElevationOutcome outcome;
        try
        {
            outcome = await launcher.LaunchAsync(ticket.Arguments, CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            outcome = FirewallElevationOutcome.CancelledByUser();
        }

        FirewallAccess = await client.SendAsync<WindowsFirewallState>(
            "CompleteFirewallAccess",
            new CompleteFirewallAccessRequest(serverId, ticket.OperationId, outcome.Cancelled,
                outcome.Ran ? outcome.ExitCode : (int)FirewallHelperExitCode.UnexpectedFailure,
                outcome.Detail, outcome.Failure)
            {
                Session = uiSession,
                ConnectivityOperation = PublicConnectivityOperation.CompleteFirewallAccess
            }).ConfigureAwait(true);
        StatusMessage = outcome.Cancelled ? "Administrator approval was cancelled; nothing changed." : FirewallTitle;
    }

    private async Task LoadFirewallAccessAsync()
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        var state = await client.SendAsync<WindowsFirewallState>(
            "GetFirewallAccess", new ServerIdRequest(serverId)).ConfigureAwait(true);
        if (SelectedServer?.Definition.Id == serverId)
            FirewallAccess = state;
    }

    /// <summary>Development-only visual review input. It contacts no Agent and changes no system state.</summary>
    internal void SetFirewallReviewState(
        ServerSnapshot server,
        RouterMappingState router,
        WindowsFirewallState firewall,
        bool showConsent,
        ExternalReachabilityState? external = null)
    {
        detailsServerId = server.Definition.Id;
        SelectedServer = server;
        SelectedNetworkMode = NetworkMode.PortForwarding;
        RouterMapping = router;
        FirewallAccess = firewall;
        ExternalReachability = external ?? new ExternalReachabilityState { ServerId = server.Definition.Id };
        ShowsFirewallConsent = showConsent;
        ShowsDirectInternetConsent = false;
        ShowsFirewallTechnicalDetails = false;
    }
}
