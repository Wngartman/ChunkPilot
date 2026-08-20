using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.App.Presentation;

/// <summary>Central beginner-facing wording for the Agent's typed Windows Firewall diagnosis.</summary>
public static class WindowsFirewallPresentation
{
    public static FirewallDiagnostic Diagnostic(WindowsFirewallState state) =>
        state.Diagnostic.Reason == FirewallDiagnosticReason.NotChecked &&
        state.Phase != FirewallAccessPhase.NotChecked
            ? WindowsFirewallDiagnostics.Evaluate(state)
            : state.Diagnostic;

    public static string Title(WindowsFirewallState state)
    {
        if (state.Phase is FirewallAccessPhase.Checking or FirewallAccessPhase.WaitingForElevation or
            FirewallAccessPhase.Configuring or FirewallAccessPhase.Removing)
            return state.Phase switch
            {
                FirewallAccessPhase.Checking => "Checking Windows Firewall",
                FirewallAccessPhase.WaitingForElevation => "Waiting for administrator approval",
                FirewallAccessPhase.Configuring => "Configuring Windows Firewall",
                _ => "Removing firewall access"
            };
        return Diagnostic(state).Reason switch
        {
            FirewallDiagnosticReason.None when state.Phase == FirewallAccessPhase.Configured => "Firewall rule ready",
            FirewallDiagnosticReason.None => "Windows Firewall permission is ready to configure",
            FirewallDiagnosticReason.NotChecked => "Windows Firewall has not been checked",
            FirewallDiagnosticReason.PublicProfileApprovalRequired => "Windows considers this a Public network",
            FirewallDiagnosticReason.NetworkProfileUnavailable or FirewallDiagnosticReason.NetworkListUnavailable =>
                "Windows network information is incomplete",
            FirewallDiagnosticReason.NetworkProfileAmbiguous or FirewallDiagnosticReason.NetworkPathAmbiguous =>
                "ChunkPilot found more than one possible network",
            FirewallDiagnosticReason.NetworkPathUnavailable => "A trusted local network could not be verified",
            FirewallDiagnosticReason.FirewallPlatformUnavailable => "Windows Firewall information isn't available",
            FirewallDiagnosticReason.FirewallPolicyIncomplete => "Windows Firewall policy couldn't be fully verified",
            FirewallDiagnosticReason.FirewallProfileDisabled => "Windows Firewall is turned off for this network",
            FirewallDiagnosticReason.LocalPolicyReadOnly when state.BlockAllInboundForProfile =>
                "Windows is blocking all incoming connections",
            FirewallDiagnosticReason.LocalPolicyManaged or FirewallDiagnosticReason.LocalPolicyReadOnly =>
                "Firewall settings are managed by your organization",
            FirewallDiagnosticReason.ExplicitBlockConflict => "A Windows Firewall rule is blocking this server",
            FirewallDiagnosticReason.UnknownBlockConstraint => "A blocking firewall rule could not be fully verified",
            FirewallDiagnosticReason.ExistingForeignAllow => "Windows Firewall access already exists",
            FirewallDiagnosticReason.OwnedRuleStale => "Firewall access needs an update",
            FirewallDiagnosticReason.OwnershipConflict => "Firewall rule ownership could not be verified",
            FirewallDiagnosticReason.JavaRuntimeUnresolved => "Java runtime couldn't be verified",
            FirewallDiagnosticReason.PortUnresolved => "Server port couldn't be verified",
            FirewallDiagnosticReason.ElevationCancelled => "Firewall setup cancelled",
            FirewallDiagnosticReason.ElevationDenied => "Windows didn't allow the firewall change",
            FirewallDiagnosticReason.HelperLaunchFailed => "Firewall helper could not start",
            FirewallDiagnosticReason.HelperMutationRejected => "Windows rejected the firewall rule",
            FirewallDiagnosticReason.RuleVerificationFailed => "The firewall rule could not be verified",
            FirewallDiagnosticReason.RemovalFailed => "Firewall access could not be removed",
            _ => "Windows Firewall needs attention"
        };
    }

    public static string Summary(WindowsFirewallState state)
    {
        if (state.Phase is FirewallAccessPhase.Checking or FirewallAccessPhase.WaitingForElevation or
            FirewallAccessPhase.Configuring or FirewallAccessPhase.Removing)
            return state.Phase switch
            {
                FirewallAccessPhase.Checking => "ChunkPilot is refreshing the server target, network path, profile, policy, and relevant rules.",
                FirewallAccessPhase.WaitingForElevation => "Windows is waiting for you to approve or cancel the one-time administrator prompt.",
                FirewallAccessPhase.Configuring => "The one-shot helper is applying the exact rule. ChunkPilot will verify every property afterward.",
                _ => "The one-shot helper is removing only the rule ChunkPilot can prove it created."
            };
        return Diagnostic(state).Reason switch
        {
            FirewallDiagnosticReason.None when state.Phase == FirewallAccessPhase.Configured =>
                $"TCP {state.Port} is allowed for this server's Java runtime on the {WindowsFirewallPolicy.DescribeProfiles(state.Profiles)} profile. External reachability has not been verified.",
            FirewallDiagnosticReason.None =>
                "ChunkPilot found the exact Java runtime, TCP port, network path, and Windows profile needed for a narrow rule.",
            FirewallDiagnosticReason.NotChecked => "Run a read-only check to inspect the current target, network, policy, and relevant rules.",
            FirewallDiagnosticReason.PublicProfileApprovalRequired =>
                $"Windows classifies {(state.InterfaceName.Length > 0 ? state.InterfaceName : "this network")} as Public. " +
                "ChunkPilot will not change that category; adding a rule requires separate approval for this exact server.",
            FirewallDiagnosticReason.NetworkProfileUnavailable or FirewallDiagnosticReason.NetworkListUnavailable =>
                "ChunkPilot found the network connection used by this server, but Windows did not provide the firewall profile needed to configure it safely.",
            FirewallDiagnosticReason.NetworkProfileAmbiguous or FirewallDiagnosticReason.NetworkPathAmbiguous =>
                "Windows has multiple active network paths and ChunkPilot can't safely determine which one should receive this server's firewall rule.",
            FirewallDiagnosticReason.NetworkPathUnavailable =>
                "ChunkPilot could not prove one ordinary routed Ethernet or Wi-Fi path for this server, so it will not guess.",
            FirewallDiagnosticReason.FirewallPlatformUnavailable =>
                "Windows did not provide firewall policy information, so ChunkPilot will not make a firewall change. Another security product may be managing incoming connections.",
            FirewallDiagnosticReason.FirewallPolicyIncomplete =>
                "Windows Firewall answered and the verified server and network details are retained, but one or more policy fields did not. ChunkPilot will not make a change until those fields can be read safely.",
            FirewallDiagnosticReason.FirewallProfileDisabled =>
                "ChunkPilot does not need to add a Windows Firewall rule while this profile is disabled. Another security product may still control incoming connections; reachability has not been verified.",
            FirewallDiagnosticReason.LocalPolicyReadOnly when state.BlockAllInboundForProfile =>
                "Windows is configured to ignore local inbound allow rules on this profile. ChunkPilot will not weaken that policy.",
            FirewallDiagnosticReason.LocalPolicyManaged or FirewallDiagnosticReason.LocalPolicyReadOnly =>
                "Windows is not allowing local firewall rules to control this network. ChunkPilot will not override your organization's policy.",
            FirewallDiagnosticReason.ExplicitBlockConflict =>
                "ChunkPilot found a blocking Windows rule that it does not own. It will not change that rule automatically.",
            FirewallDiagnosticReason.UnknownBlockConstraint =>
                "A potentially applicable blocking rule has user, package, interface, or security conditions ChunkPilot cannot safely interpret. No firewall-ready claim or change will be made.",
            FirewallDiagnosticReason.ExistingForeignAllow =>
                "An existing Windows rule allows this server's required connection. ChunkPilot will leave that rule unchanged; external reachability has not been verified.",
            FirewallDiagnosticReason.OwnedRuleStale =>
                "This server's port, Java runtime, or network profile changed after the firewall rule was created.",
            FirewallDiagnosticReason.OwnershipConflict =>
                "ChunkPilot found a rule using the expected identifier but cannot prove that it created it. It will not overwrite or remove that rule.",
            FirewallDiagnosticReason.JavaRuntimeUnresolved =>
                $"ChunkPilot knows which port this server uses{PortSuffix(state)}, but it could not safely identify the Java executable that should receive the firewall permission.",
            FirewallDiagnosticReason.PortUnresolved =>
                "ChunkPilot will not create a broad firewall rule without knowing the exact Minecraft port.",
            FirewallDiagnosticReason.ElevationCancelled => "Nothing was changed. You can try again whenever you're ready.",
            FirewallDiagnosticReason.ElevationDenied => "ChunkPilot couldn't get permission to add this server's firewall rule. No verified rule was created.",
            FirewallDiagnosticReason.HelperLaunchFailed => "The trusted one-shot firewall helper did not start. No firewall change was attempted.",
            FirewallDiagnosticReason.HelperMutationRejected => "Windows or the helper rejected the exact requested rule. No verified rule was created.",
            FirewallDiagnosticReason.RuleVerificationFailed => "The operation finished, but Windows does not show the exact rule ChunkPilot requested, so it is not reported as configured.",
            FirewallDiagnosticReason.RemovalFailed => "Windows still lists the owned rule. ChunkPilot retained its ownership evidence so removal can be retried safely.",
            _ => "ChunkPilot could not establish a verified firewall state and will not guess or broaden the rule."
        };
    }

    public static AppTone Tone(WindowsFirewallState state) => Diagnostic(state).Severity switch
    {
        FirewallDiagnosticSeverity.Success => AppTone.Success,
        FirewallDiagnosticSeverity.Info => AppTone.Info,
        FirewallDiagnosticSeverity.Warning or FirewallDiagnosticSeverity.Error => AppTone.Warning,
        _ => state.Busy ? AppTone.Accent : AppTone.Neutral
    };

    public static string Badge(WindowsFirewallState state) => Diagnostic(state).Reason switch
    {
        FirewallDiagnosticReason.None when state.Phase == FirewallAccessPhase.Configured => "Rule ready",
        FirewallDiagnosticReason.None when state.Busy => "Working",
        FirewallDiagnosticReason.None => "Permission needed",
        FirewallDiagnosticReason.PublicProfileApprovalRequired => "Public network",
        FirewallDiagnosticReason.ExistingForeignAllow => "Existing rule",
        FirewallDiagnosticReason.FirewallPolicyIncomplete => "Policy incomplete",
        FirewallDiagnosticReason.FirewallProfileDisabled => "Firewall off",
        FirewallDiagnosticReason.LocalPolicyReadOnly when state.BlockAllInboundForProfile => "Inbound blocked",
        FirewallDiagnosticReason.LocalPolicyManaged or FirewallDiagnosticReason.LocalPolicyReadOnly => "Managed",
        FirewallDiagnosticReason.ExplicitBlockConflict => "Blocked",
        FirewallDiagnosticReason.UnknownBlockConstraint => "Unverified block",
        FirewallDiagnosticReason.OwnedRuleStale => "Update needed",
        FirewallDiagnosticReason.ElevationCancelled => "Cancelled",
        FirewallDiagnosticReason.NotChecked => "Not checked",
        _ => "Attention"
    };

    public static string ActionText(FirewallRecoveryAction action) => action switch
    {
        FirewallRecoveryAction.CheckAgain => "Check again",
        FirewallRecoveryAction.Configure => "Allow through Windows Firewall",
        FirewallRecoveryAction.ConfirmPublic => "Review Public access",
        FirewallRecoveryAction.Update => "Update firewall access",
        FirewallRecoveryAction.RetryRemoval => "Retry firewall removal",
        FirewallRecoveryAction.ReviewJava => "Review Java",
        FirewallRecoveryAction.ReviewPort => "Review server port",
        FirewallRecoveryAction.ViewDetails => "View details",
        FirewallRecoveryAction.TryAgain => "Try again",
        _ => ""
    };

    public static bool CanConfigure(WindowsFirewallState state) => !state.Busy && state.Phase is
        FirewallAccessPhase.NeedsPermission or FirewallAccessPhase.PublicNetworkConfirmationRequired or
        FirewallAccessPhase.Stale or FirewallAccessPhase.NeedsAttention && state.Configured;

    /// <summary>
    /// The same line, corrected by evidence from outside the network when there is any.
    /// </summary>
    /// <remarks>
    /// The two-argument form is still the truth whenever nothing has been verified externally; this
    /// overload exists so that a line saying reachability has not been verified cannot sit directly
    /// above one saying it has. It consumes the external state and rewrites neither layer's own.
    /// </remarks>
    public static string CombinedStatus(
        RouterMappingState router, WindowsFirewallState firewall, ExternalReachabilityState external)
    {
        ArgumentNullException.ThrowIfNull(external);
        if (!external.IsVerified)
            return CombinedStatus(router, firewall);
        return firewall.Phase == FirewallAccessPhase.Configured
            ? "Router and Windows Firewall are configured, and this endpoint answered from outside your network " +
              "when it was checked."
            : "This endpoint answered from outside your network when it was checked, whatever Windows Firewall " +
              "reports locally.";
    }

    public static string CombinedStatus(RouterMappingState router, WindowsFirewallState firewall) =>
        router.Phase == RouterMappingPhase.Active && firewall.Phase == FirewallAccessPhase.Configured
            ? "Router and Windows Firewall are configured. External reachability has not been verified."
            : router.Phase == RouterMappingPhase.Active
                ? "Your router is configured, but Windows Firewall is incomplete. External reachability has not been verified."
                : firewall.Phase == FirewallAccessPhase.Configured
                    ? "Windows Firewall is configured; the router is not currently mapped. External reachability has not been verified."
                    : "Router and Windows Firewall are separate layers. External reachability has not been verified.";

    private static string PortSuffix(WindowsFirewallState state) => state.Port > 0 ? $" (TCP {state.Port})" : "";
}
