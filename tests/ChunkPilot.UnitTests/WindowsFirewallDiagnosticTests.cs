using ChunkPilot.App.Presentation;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class WindowsFirewallDiagnosticTests
{
    public static IEnumerable<object[]> SupportedReasons()
    {
        yield return Case(State(FirewallAccessPhase.Configured), FirewallDiagnosticReason.None,
            "Firewall rule ready", FirewallRecoveryAction.None, FirewallDiagnosticSeverity.Success);
        yield return Case(new WindowsFirewallState(), FirewallDiagnosticReason.NotChecked,
            "Windows Firewall has not been checked", FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Neutral);
        yield return Case(State(FirewallAccessPhase.PublicNetworkConfirmationRequired) with
            { SelectedProfile = FirewallProfile.Public }, FirewallDiagnosticReason.PublicProfileApprovalRequired,
            "Windows considers this a Public network", FirewallRecoveryAction.ConfirmPublic, FirewallDiagnosticSeverity.Warning);
        yield return Case(Network(FirewallTargetProblem.NetworkProfileUnavailable),
            FirewallDiagnosticReason.NetworkProfileUnavailable, "Windows network information is incomplete",
            FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Warning);
        yield return Case(Network(FirewallTargetProblem.NetworkProfileAmbiguous),
            FirewallDiagnosticReason.NetworkProfileAmbiguous, "ChunkPilot found more than one possible network",
            FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Warning);
        yield return Case(Network(FirewallTargetProblem.NetworkPathUnavailable) with
            { NetworkPathStatus = NetworkPathStatus.Unavailable }, FirewallDiagnosticReason.NetworkPathUnavailable,
            "A trusted local network could not be verified", FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Warning);
        yield return Case(Network(FirewallTargetProblem.NetworkPathAmbiguous) with
            { NetworkPathStatus = NetworkPathStatus.Ambiguous }, FirewallDiagnosticReason.NetworkPathAmbiguous,
            "ChunkPilot found more than one possible network", FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Warning);
        yield return Case(Network(FirewallTargetProblem.NetworkListUnavailable) with
            { NetworkPathStatus = NetworkPathStatus.Available, NetworkListStatus = NetworkListStatus.ReadFailed },
            FirewallDiagnosticReason.NetworkListUnavailable, "Windows network information is incomplete",
            FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.Unsupported) with
            { FirewallApiAvailable = false, FirewallPlatformStatus = FirewallPlatformStatus.ReadFailed },
            FirewallDiagnosticReason.FirewallPlatformUnavailable, "Windows Firewall information isn't available",
            FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.NeedsAttention) with
            { FirewallPolicyUnavailableFields = FirewallPolicyUnavailableFields.Rules },
            FirewallDiagnosticReason.FirewallPolicyIncomplete,
            "Windows Firewall policy couldn't be fully verified",
            FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.FirewallDisabled) with { FirewallEnabledForProfile = false },
            FirewallDiagnosticReason.FirewallProfileDisabled, "Windows Firewall is turned off for this network",
            FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Info);
        yield return Case(State(FirewallAccessPhase.ManagedByOrganization) with
            { ModifyState = FirewallPolicyModifyState.GroupPolicyOverride }, FirewallDiagnosticReason.LocalPolicyManaged,
            "Firewall settings are managed by your organization", FirewallRecoveryAction.ViewDetails,
            FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.ManagedByOrganization) with
            { ModifyState = FirewallPolicyModifyState.InboundBlocked }, FirewallDiagnosticReason.LocalPolicyReadOnly,
            "Firewall settings are managed by your organization", FirewallRecoveryAction.ViewDetails,
            FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.BlockedByPolicy) with { BlockingRuleName = "Administrator block" },
            FirewallDiagnosticReason.ExplicitBlockConflict, "A Windows Firewall rule is blocking this server",
            FirewallRecoveryAction.ViewDetails, FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.NeedsAttention) with
            {
                BlockingRuleName = "Conditional block",
                BlockingRuleCoverage = FirewallRuleCoverage.UnknownOrUnsupported
            }, FirewallDiagnosticReason.UnknownBlockConstraint,
            "A blocking firewall rule could not be fully verified",
            FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.ExistingWindowsRule) with { ExistingRuleName = "Existing allow" },
            FirewallDiagnosticReason.ExistingForeignAllow, "Windows Firewall access already exists",
            FirewallRecoveryAction.None, FirewallDiagnosticSeverity.Success);
        yield return Case(State(FirewallAccessPhase.Stale) with { Configured = true, StaleReasons = ["port changed"] },
            FirewallDiagnosticReason.OwnedRuleStale, "Firewall access needs an update",
            FirewallRecoveryAction.Update, FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.OwnershipConflict), FirewallDiagnosticReason.OwnershipConflict,
            "Firewall rule ownership could not be verified", FirewallRecoveryAction.ViewDetails,
            FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.RuntimeUnavailable) with
            { Failure = FirewallAccessFailure.RuntimeUnavailable, ProgramPath = "" },
            FirewallDiagnosticReason.JavaRuntimeUnresolved, "Java runtime couldn't be verified",
            FirewallRecoveryAction.ReviewJava, FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.PortUnavailable) with
            { Failure = FirewallAccessFailure.PortUnavailable, Port = 0 },
            FirewallDiagnosticReason.PortUnresolved, "Server port couldn't be verified",
            FirewallRecoveryAction.ReviewPort, FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.NeedsPermission) with { Failure = FirewallAccessFailure.Cancelled },
            FirewallDiagnosticReason.ElevationCancelled, "Firewall setup cancelled",
            FirewallRecoveryAction.TryAgain, FirewallDiagnosticSeverity.Info);
        yield return Case(State(FirewallAccessPhase.NeedsAttention) with { Failure = FirewallAccessFailure.AccessDenied },
            FirewallDiagnosticReason.ElevationDenied, "Windows didn't allow the firewall change",
            FirewallRecoveryAction.TryAgain, FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.NeedsAttention) with { Failure = FirewallAccessFailure.HelperUnavailable },
            FirewallDiagnosticReason.HelperLaunchFailed, "Firewall helper could not start",
            FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Error);
        yield return Case(State(FirewallAccessPhase.NeedsAttention) with { Failure = FirewallAccessFailure.HelperFailed },
            FirewallDiagnosticReason.HelperMutationRejected, "Windows rejected the firewall rule",
            FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Error);
        yield return Case(State(FirewallAccessPhase.NeedsAttention) with { Failure = FirewallAccessFailure.VerificationFailed },
            FirewallDiagnosticReason.RuleVerificationFailed, "The firewall rule could not be verified",
            FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Error);
        yield return Case(State(FirewallAccessPhase.NeedsAttention) with
            { Failure = FirewallAccessFailure.RemovalFailed, RemovalPending = true },
            FirewallDiagnosticReason.RemovalFailed, "Firewall access could not be removed",
            FirewallRecoveryAction.RetryRemoval, FirewallDiagnosticSeverity.Warning);
        yield return Case(State(FirewallAccessPhase.NeedsAttention) with { Failure = FirewallAccessFailure.Unknown },
            FirewallDiagnosticReason.FirewallStateUnknown, "Windows Firewall needs attention",
            FirewallRecoveryAction.CheckAgain, FirewallDiagnosticSeverity.Warning);
    }

    [Theory]
    [MemberData(nameof(SupportedReasons))]
    public void Every_supported_reason_has_concise_central_presentation(
        WindowsFirewallState state,
        FirewallDiagnosticReason expectedReason,
        string expectedTitle,
        FirewallRecoveryAction expectedAction,
        FirewallDiagnosticSeverity expectedSeverity)
    {
        var diagnostic = WindowsFirewallDiagnostics.Evaluate(state);
        state = state with { Diagnostic = diagnostic };

        Assert.Equal(expectedReason, diagnostic.Reason);
        Assert.Equal(expectedAction, diagnostic.PrimaryAction);
        Assert.Equal(expectedSeverity, diagnostic.Severity);
        Assert.Equal(expectedTitle, WindowsFirewallPresentation.Title(state));
        Assert.InRange(WindowsFirewallPresentation.Summary(state).Length, 10, 260);
        Assert.DoesNotContain("HRESULT", WindowsFirewallPresentation.Summary(state), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COMException", WindowsFirewallPresentation.Summary(state), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Managed_policy_outranks_public_and_java_outranks_public()
    {
        var managedPublic = State(FirewallAccessPhase.PublicNetworkConfirmationRequired) with
        {
            ModifyState = FirewallPolicyModifyState.GroupPolicyOverride,
            SelectedProfile = FirewallProfile.Public
        };
        var javaPublic = State(FirewallAccessPhase.RuntimeUnavailable) with
        {
            Failure = FirewallAccessFailure.RuntimeUnavailable,
            SelectedProfile = FirewallProfile.Public
        };

        Assert.Equal(FirewallDiagnosticReason.LocalPolicyManaged,
            WindowsFirewallDiagnostics.Evaluate(managedPublic).Reason);
        Assert.Equal(FirewallDiagnosticReason.JavaRuntimeUnresolved,
            WindowsFirewallDiagnostics.Evaluate(javaPublic).Reason);
    }

    [Fact]
    public void Block_all_inbound_has_specific_copy_without_claiming_group_policy()
    {
        var state = State(FirewallAccessPhase.ManagedByOrganization) with
        {
            BlockAllInboundForProfile = true
        };
        state = state with { Diagnostic = WindowsFirewallDiagnostics.Evaluate(state) };

        Assert.Equal(FirewallDiagnosticReason.LocalPolicyReadOnly, state.Diagnostic.Reason);
        Assert.Equal("Windows is blocking all incoming connections",
            WindowsFirewallPresentation.Title(state));
        Assert.DoesNotContain("organization", WindowsFirewallPresentation.Summary(state),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Technical_evidence_is_independent_of_primary_java_failure()
    {
        var state = State(FirewallAccessPhase.RuntimeUnavailable) with
        {
            Failure = FirewallAccessFailure.RuntimeUnavailable,
            Port = 25566,
            SelectedProfile = FirewallProfile.Public,
            InterfaceName = "Ethernet",
            InterfaceIndex = 16,
            LocalAddress = "10.0.0.140",
            GatewayAddress = "10.0.0.1"
        };

        Assert.Equal(FirewallDiagnosticReason.JavaRuntimeUnresolved,
            WindowsFirewallDiagnostics.Evaluate(state).Reason);
        Assert.Equal(25566, state.Port);
        Assert.Equal(FirewallProfile.Public, state.SelectedProfile);
        Assert.Equal(16, state.InterfaceIndex);
    }

    private static object[] Case(WindowsFirewallState state, FirewallDiagnosticReason reason,
        string title, FirewallRecoveryAction action, FirewallDiagnosticSeverity severity) =>
        [state, reason, title, action, severity];

    private static WindowsFirewallState Network(FirewallTargetProblem problem) =>
        State(FirewallAccessPhase.NetworkUnavailable) with
        {
            Failure = FirewallAccessFailure.NetworkUnavailable,
            TargetProblem = problem,
            NetworkPathStatus = NetworkPathStatus.Available
        };

    private static WindowsFirewallState State(FirewallAccessPhase phase) => new()
    {
        ServerId = Guid.Parse("A43C5B87-8D32-421D-82B7-3B43425B4A5B"),
        Phase = phase,
        FirewallApiAvailable = true,
        FirewallPlatformStatus = FirewallPlatformStatus.Available,
        FirewallEnabledForProfile = true,
        ModifyState = FirewallPolicyModifyState.Ok,
        ProgramPath = @"D:\ChunkPilot\managed-java\bin\java.exe",
        JavaVerified = true,
        Port = 25566,
        PortVerified = true,
        SelectedProfile = FirewallProfile.Private,
        NetworkProfileVerified = true,
        Profiles = FirewallProfile.Private,
        InterfaceName = "Ethernet",
        InterfaceIndex = 16,
        LocalAddress = "10.0.0.140",
        GatewayAddress = "10.0.0.1",
        NetworkPathStatus = NetworkPathStatus.Available,
        NetworkListStatus = NetworkListStatus.Available
    };
}
