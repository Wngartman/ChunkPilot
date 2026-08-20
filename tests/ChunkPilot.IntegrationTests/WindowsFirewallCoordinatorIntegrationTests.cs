using System.Net;
using System.Net.NetworkInformation;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// Real coordinator + SQLite + supervisor with read-only fake Windows views. No test in this class
/// opens the real firewall policy or invokes the elevated helper.
/// </summary>
public sealed class WindowsFirewallCoordinatorIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "chunkpilot-firewall-agent-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task New_server_is_read_only_and_needs_explicit_permission()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);

        var state = await h.Coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.NeedsPermission, state.Phase);
        Assert.Null(await h.Store.GetFirewallAccessAsync(id));
        Assert.Empty(h.Policy.Snapshot.Rules);
    }

    [Fact]
    public async Task Uac_cancellation_is_informational_and_persists_no_intent()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var ticket = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None);

        var state = await h.Coordinator.CompleteAsync(id, ticket.OperationId, true, 0, "cancelled", CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.NeedsPermission, state.Phase);
        Assert.Equal(FirewallAccessFailure.Cancelled, state.Failure);
        Assert.Equal(FirewallDiagnosticReason.ElevationCancelled, state.Diagnostic.Reason);
        Assert.Null(await h.Store.GetFirewallAccessAsync(id));

        var refreshed = await h.Coordinator.CheckAsync(id, CancellationToken.None);
        Assert.Equal(FirewallAccessFailure.None, refreshed.Failure);
        Assert.Equal(FirewallDiagnosticReason.None, refreshed.Diagnostic.Reason);
    }

    [Theory]
    [InlineData(FirewallElevationFailure.HelperUnavailable, FirewallAccessFailure.HelperUnavailable,
        FirewallDiagnosticReason.HelperLaunchFailed)]
    [InlineData(FirewallElevationFailure.PermissionDenied, FirewallAccessFailure.ElevationFailed,
        FirewallDiagnosticReason.ElevationDenied)]
    [InlineData(FirewallElevationFailure.LaunchFailed, FirewallAccessFailure.HelperUnavailable,
        FirewallDiagnosticReason.HelperLaunchFailed)]
    public async Task Elevation_launch_outcomes_are_typed_without_parsing_launcher_text(
        FirewallElevationFailure elevationFailure,
        FirewallAccessFailure expectedFailure,
        FirewallDiagnosticReason expectedReason)
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var ticket = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false,
            CancellationToken.None);

        var state = await h.Coordinator.CompleteAsync(id, ticket.OperationId, false,
            (int)FirewallHelperExitCode.UnexpectedFailure, "opaque launcher evidence", elevationFailure,
            CancellationToken.None);

        Assert.Equal(expectedFailure, state.Failure);
        Assert.Equal(expectedReason, state.Diagnostic.Reason);
        Assert.False(state.Configured);
        Assert.Empty(h.Policy.Snapshot.Rules);
    }

    [Fact]
    public async Task Helper_exit_without_verified_rule_is_never_success()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var ticket = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None);

        var state = await h.Coordinator.CompleteAsync(id, ticket.OperationId, false,
            (int)FirewallHelperExitCode.Applied, "helper said yes", CancellationToken.None);

        Assert.NotEqual(FirewallAccessPhase.Configured, state.Phase);
        Assert.Equal(FirewallAccessFailure.VerificationFailed, state.Failure);
        Assert.False((await h.Store.GetFirewallAccessAsync(id))!.Configured);
    }

    [Fact]
    public async Task Create_is_ready_only_after_exact_postcondition_and_survives_restart()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var ticket = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None);
        h.ApplyTicket(ticket);

        var state = await h.Coordinator.CompleteAsync(id, ticket.OperationId, false,
            (int)FirewallHelperExitCode.Applied, "fixture", CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.Configured, state.Phase);
        Assert.Equal(FirewallRuleOwner.ChunkPilot, state.Owner);
        Assert.Equal(FirewallProfile.Private, state.Profiles);
        Assert.Equal(25566, state.Port);

        await h.RestartCoordinatorAsync();
        Assert.Equal(FirewallAccessPhase.Configured,
            (await h.Coordinator.GetStateAsync(id, CancellationToken.None)).Phase);
    }

    [Fact]
    public async Task Double_prepare_does_not_replace_the_operation_in_flight()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);

        var first = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None);
        var second = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None);

        Assert.True(first.Ready);
        Assert.False(second.Ready);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(FirewallAccessPhase.WaitingForElevation, second.State.Phase);
    }

    [Fact]
    public async Task Cancelled_or_superseded_completion_cannot_change_state()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var ticket = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None);
        _ = await h.Coordinator.CancelAsync(id, CancellationToken.None);
        h.ApplyTicket(ticket);

        var stale = await h.Coordinator.CompleteAsync(id, ticket.OperationId, false,
            (int)FirewallHelperExitCode.Applied, "late", CancellationToken.None);

        Assert.NotEqual(FirewallAccessPhase.Configured, stale.Phase);
        Assert.Null(await h.Store.GetFirewallAccessAsync(id));
    }

    [Fact]
    public async Task Public_profile_requires_a_second_explicit_approval()
    {
        await using var h = await Harness.StartAsync(root);
        h.Category.Category = WindowsNetworkCategory.Public;
        h.Policy.Snapshot = h.Policy.Snapshot with { CurrentProfiles = FirewallProfile.Public };
        var id = await h.AddServerAsync(25566);
        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);

        var refused = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None);
        var approved = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, true, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.PublicNetworkConfirmationRequired, state.Phase);
        Assert.False(refused.Ready);
        Assert.True(approved.Ready);
        var command = Assert.IsType<FirewallHelperCommand>(FirewallHelperCommandParser.Parse(approved.Arguments).Command);
        Assert.Equal(FirewallProfile.Public, command.Profiles);
    }

    [Theory]
    [InlineData(FirewallPolicyUnavailableFields.CurrentProfiles)]
    [InlineData(FirewallPolicyUnavailableFields.FirewallEnabled)]
    [InlineData(FirewallPolicyUnavailableFields.LocalPolicyModifyState)]
    [InlineData(FirewallPolicyUnavailableFields.BlockAllInboundTraffic)]
    [InlineData(FirewallPolicyUnavailableFields.Rules)]
    public async Task Partial_policy_read_keeps_public_target_evidence_and_fails_mutation_closed(
        FirewallPolicyUnavailableFields unavailableField)
    {
        await using var h = await Harness.StartAsync(root);
        h.Category.Category = WindowsNetworkCategory.Public;
        h.Policy.Snapshot = h.Policy.Snapshot with
        {
            CurrentProfiles = FirewallProfile.Public,
            UnavailableFields = unavailableField,
            FirewallEnabledUnavailableProfiles = unavailableField == FirewallPolicyUnavailableFields.FirewallEnabled
                ? FirewallProfile.Public
                : FirewallProfile.None,
            BlockAllInboundUnavailableProfiles =
                unavailableField == FirewallPolicyUnavailableFields.BlockAllInboundTraffic
                    ? FirewallProfile.Public
                    : FirewallProfile.None
        };
        var id = await h.AddServerAsync(25566);

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);
        var ticket = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, true,
            CancellationToken.None);

        Assert.True(state.FirewallApiAvailable);
        Assert.Equal(FirewallPlatformStatus.Available, state.FirewallPlatformStatus);
        Assert.Equal(unavailableField, state.FirewallPolicyUnavailableFields);
        Assert.Equal(FirewallDiagnosticReason.FirewallPolicyIncomplete, state.Diagnostic.Reason);
        Assert.Equal(FirewallProfile.Public, state.SelectedProfile);
        Assert.Equal(25566, state.Port);
        Assert.Equal(h.Java1, state.ProgramPath);
        Assert.Equal("Ethernet", state.InterfaceName);
        Assert.Equal(16, state.InterfaceIndex);
        Assert.Equal("192.168.1.50", state.LocalAddress);
        Assert.Equal("192.168.1.1", state.GatewayAddress);
        Assert.False(ticket.Ready);
        Assert.Empty(h.Policy.Snapshot.Rules);
    }

    [Theory]
    [InlineData(WindowsNetworkCategory.Private, FirewallProfile.Private)]
    [InlineData(WindowsNetworkCategory.DomainAuthenticated, FirewallProfile.Domain)]
    public async Task Private_and_domain_profiles_use_the_normal_consent_path(
        WindowsNetworkCategory category, FirewallProfile profile)
    {
        await using var h = await Harness.StartAsync(root);
        h.Category.Category = category;
        h.Policy.Snapshot = h.Policy.Snapshot with { CurrentProfiles = profile };
        var id = await h.AddServerAsync(25566);

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.NeedsPermission, state.Phase);
        Assert.Equal(profile, state.SelectedProfile);
        Assert.Equal(25566, state.Port);
        Assert.Equal(h.Java1, state.ProgramPath);
    }

    [Fact]
    public async Task Missing_profile_retains_authoritative_managed_runtime_and_custom_port()
    {
        await using var h = await Harness.StartAsync(root);
        h.Category.Available = false;
        var id = await h.AddServerAsync(25566);

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.NetworkUnavailable, state.Phase);
        Assert.Equal(FirewallAccessFailure.NetworkUnavailable, state.Failure);
        Assert.Equal(25566, state.Port);
        Assert.True(state.PortVerified);
        Assert.Equal(h.Java1, state.ProgramPath);
        Assert.True(state.JavaVerified);
        Assert.Equal(FirewallProfile.None, state.SelectedProfile);
        Assert.Equal(16, state.InterfaceIndex);
        Assert.Equal("192.168.1.50", state.LocalAddress);
        Assert.Equal("192.168.1.1", state.GatewayAddress);
        Assert.Equal(FirewallDiagnosticReason.NetworkProfileUnavailable, state.Diagnostic.Reason);
    }

    [Fact]
    public async Task Nlm_read_failure_is_distinct_and_keeps_independent_path_evidence()
    {
        await using var h = await Harness.StartAsync(root);
        h.Category.Status = NetworkListStatus.ReadFailed;
        var id = await h.AddServerAsync(25566);

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(FirewallDiagnosticReason.NetworkListUnavailable, state.Diagnostic.Reason);
        Assert.Equal(NetworkListStatus.ReadFailed, state.NetworkListStatus);
        Assert.Equal(NetworkPathStatus.Available, state.NetworkPathStatus);
        Assert.Equal(16, state.InterfaceIndex);
        Assert.Equal("192.168.1.50", state.LocalAddress);
        Assert.Equal("192.168.1.1", state.GatewayAddress);
        Assert.True(state.JavaVerified);
        Assert.True(state.PortVerified);
    }

    [Theory]
    [InlineData(FirewallPolicyModifyState.GroupPolicyOverride)]
    [InlineData(FirewallPolicyModifyState.InboundBlocked)]
    public async Task Managed_policy_is_reported_and_never_prepared(FirewallPolicyModifyState modify)
    {
        await using var h = await Harness.StartAsync(root);
        h.Policy.Snapshot = h.Policy.Snapshot with { ModifyState = modify };
        var id = await h.AddServerAsync(25566);

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);
        var ticket = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.ManagedByOrganization, state.Phase);
        Assert.False(ticket.Ready);
    }

    [Fact]
    public async Task Block_all_inbound_is_distinct_and_never_competed_with()
    {
        await using var h = await Harness.StartAsync(root);
        h.Policy.Snapshot = h.Policy.Snapshot with { BlockAllInboundProfiles = FirewallProfile.Private };
        var id = await h.AddServerAsync(25566);

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);
        var ticket = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false,
            CancellationToken.None);

        Assert.True(state.BlockAllInboundForProfile);
        Assert.Equal(FirewallDiagnosticReason.LocalPolicyReadOnly, state.Diagnostic.Reason);
        Assert.False(ticket.Ready);
        Assert.Empty(h.Policy.Snapshot.Rules);
    }

    [Fact]
    public async Task Disabled_firewall_is_truthful_and_not_enabled_by_chunkpilot()
    {
        await using var h = await Harness.StartAsync(root);
        h.Policy.Snapshot = h.Policy.Snapshot with { EnabledProfiles = FirewallProfile.None };
        var id = await h.AddServerAsync(25566);

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.FirewallDisabled, state.Phase);
        Assert.False((await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None)).Ready);
        Assert.Equal(FirewallProfile.None, h.Policy.Snapshot.EnabledProfiles);
    }

    [Fact]
    public async Task Mismatched_active_profile_is_not_guessed_or_authorized()
    {
        await using var h = await Harness.StartAsync(root);
        h.Policy.Snapshot = h.Policy.Snapshot with { CurrentProfiles = FirewallProfile.Public };
        var id = await h.AddServerAsync(25566);

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);
        var ticket = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.NeedsAttention, state.Phase);
        Assert.False(ticket.Ready);
    }

    [Fact]
    public async Task Foreign_allow_is_reported_and_never_taken_over()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var plan = h.TargetPlan(id, Guid.NewGuid(), 25566);
        h.Policy.Snapshot = h.Policy.Snapshot with
        {
            Rules = [Harness.Exact(plan) with { Name = "Administrator Java", Grouping = "Admin", Description = "foreign" }]
        };

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.ExistingWindowsRule, state.Phase);
        Assert.Equal(FirewallRuleOwner.ExistingWindowsRule, state.Owner);
        Assert.Equal(FirewallRuleCoverage.ExactEquivalent, state.ExistingRuleCoverage);
        Assert.Null(await h.Store.GetFirewallAccessAsync(id));
    }

    [Fact]
    public async Task Broader_foreign_allow_is_evidence_only_and_exact_setup_remains_available()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var plan = h.TargetPlan(id, Guid.NewGuid(), 25566);
        h.Policy.Snapshot = h.Policy.Snapshot with
        {
            Rules = [Harness.Exact(plan) with
            {
                Name = "Administrator broad Java",
                Grouping = "Admin",
                Description = "foreign",
                LocalPorts = "*",
                ApplicationName = ""
            }]
        };

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.NeedsPermission, state.Phase);
        Assert.Equal(FirewallRuleOwner.None, state.Owner);
        Assert.Equal(FirewallRuleCoverage.None, state.ExistingRuleCoverage);
        Assert.Equal("Administrator broad Java", state.OtherAllowRuleName);
        Assert.Equal(FirewallRuleCoverage.BroadUnrestricted, state.OtherAllowRuleCoverage);
        Assert.True((await h.Coordinator.PrepareAsync(
            id, FirewallHelperOperation.Create, false, CancellationToken.None)).Ready);
        Assert.Single(h.Policy.Snapshot.Rules);
    }

    [Fact]
    public async Task Adobe_style_user_owned_allow_does_not_suppress_public_consent_or_exact_setup()
    {
        await using var h = await Harness.StartAsync(root);
        h.Category.Category = WindowsNetworkCategory.Public;
        h.Policy.Snapshot = h.Policy.Snapshot with { CurrentProfiles = FirewallProfile.Public };
        var id = await h.AddServerAsync(25566);
        var plan = h.TargetPlan(id, Guid.NewGuid(), 25566);
        var adobe = Harness.Exact(plan) with
        {
            Name = "Adobe Native Client",
            Description = "Adobe Native Client",
            Grouping = "Adobe Native Client",
            ApplicationName = "",
            Protocol = WindowsFirewallPolicy.ProtocolAny,
            LocalPorts = "",
            Profiles = FirewallProfile.Domain | FirewallProfile.Private | FirewallProfile.Public,
            EdgeTraversal = true,
            EdgeTraversalOptions = 1,
            LocalUserOwner = "S-1-5-21-111-222-333-1002"
        };
        h.Policy.Snapshot = h.Policy.Snapshot with { Rules = [adobe] };

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.PublicNetworkConfirmationRequired, state.Phase);
        Assert.Equal(FirewallDiagnosticReason.PublicProfileApprovalRequired, state.Diagnostic.Reason);
        Assert.Equal(FirewallRuleOwner.None, state.Owner);
        Assert.Equal(FirewallRuleCoverage.None, state.ExistingRuleCoverage);
        Assert.Equal(adobe.Name, state.OtherAllowRuleName);
        Assert.Equal(FirewallRuleCoverage.UnknownOrUnsupported, state.OtherAllowRuleCoverage);
        Assert.False((await h.Coordinator.PrepareAsync(
            id, FirewallHelperOperation.Create, false, CancellationToken.None)).Ready);
        Assert.True((await h.Coordinator.PrepareAsync(
            id, FirewallHelperOperation.Create, true, CancellationToken.None)).Ready);
        Assert.Same(adobe, h.Policy.Snapshot.Rules.Single());
    }

    [Fact]
    public async Task Foreign_block_overrides_allow_presentation_and_is_untouched()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var plan = h.TargetPlan(id, Guid.NewGuid(), 25566);
        var block = Harness.Exact(plan) with
        {
            Name = "Administrator block", Grouping = "Admin", Description = "foreign",
            Action = FirewallRuleAction.Block
        };
        h.Policy.Snapshot = h.Policy.Snapshot with { Rules = [block] };

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.BlockedByPolicy, state.Phase);
        Assert.Equal(block.Name, state.BlockingRuleName);
        Assert.Same(block, h.Policy.Snapshot.Rules.Single());
    }

    [Fact]
    public async Task Unknown_potentially_applicable_block_prevents_false_ready_state()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var plan = h.TargetPlan(id, Guid.NewGuid(), 25566);
        var block = Harness.Exact(plan) with
        {
            Name = "Conditional administrator block",
            Grouping = "Admin",
            Description = "foreign",
            Action = FirewallRuleAction.Block,
            UnavailableFields = FirewallRuleUnavailableFields.LocalUserAuthorizedList
        };
        h.Policy.Snapshot = h.Policy.Snapshot with { Rules = [block] };

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.NeedsAttention, state.Phase);
        Assert.Equal(FirewallDiagnosticReason.UnknownBlockConstraint, state.Diagnostic.Reason);
        Assert.Equal(FirewallRuleCoverage.UnknownOrUnsupported, state.BlockingRuleCoverage);
        Assert.False((await h.Coordinator.PrepareAsync(
            id, FirewallHelperOperation.Create, false, CancellationToken.None)).Ready);
        Assert.Same(block, h.Policy.Snapshot.Rules.Single());
    }

    [Fact]
    public async Task Port_runtime_and_profile_changes_make_one_stable_rule_stale_until_update()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var created = await h.CreateExactAsync(id);
        var originalRuleId = created.RuleId;

        await h.UpdateServerAsync(id, 25567, h.Java2);
        h.Category.Category = WindowsNetworkCategory.Public;
        h.Policy.Snapshot = h.Policy.Snapshot with { CurrentProfiles = FirewallProfile.Public };
        var stale = await h.Coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.Stale, stale.Phase);
        Assert.True(stale.StaleReasons.Count >= 3);
        var update = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Update, true, CancellationToken.None);
        var command = Assert.IsType<FirewallHelperCommand>(FirewallHelperCommandParser.Parse(update.Arguments).Command);
        Assert.Equal(originalRuleId, command.RuleId);
        Assert.Equal(25567, command.Port);
        Assert.Equal(h.Java2, command.ProgramPath);
        Assert.Equal(FirewallProfile.Public, command.Profiles);
        h.ApplyTicket(update);
        var ready = await h.Coordinator.CompleteAsync(id, update.OperationId, false,
            (int)FirewallHelperExitCode.Applied, "updated", CancellationToken.None);
        Assert.Equal(FirewallAccessPhase.Configured, ready.Phase);
        Assert.Equal(originalRuleId, ready.RuleId);
    }

    [Fact]
    public async Task Target_change_while_uac_is_open_is_recorded_as_stale_not_ready()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var ticket = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None);
        await h.UpdateServerAsync(id, 25567, h.Java1);
        h.ApplyTicket(ticket);

        var state = await h.Coordinator.CompleteAsync(id, ticket.OperationId, false,
            (int)FirewallHelperExitCode.Applied, "late approval", CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.Stale, state.Phase);
        Assert.Equal(FirewallAccessFailure.TargetChanged, state.Failure);
    }

    [Fact]
    public async Task External_delete_or_disable_is_never_silently_repaired()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        _ = await h.CreateExactAsync(id);

        h.Policy.Snapshot = h.Policy.Snapshot with { Rules = [] };
        var deleted = await h.Coordinator.SynchronizeAsync(id, CancellationToken.None);
        Assert.Equal(FirewallAccessPhase.NeedsAttention, deleted.Phase);
        Assert.Empty(h.Policy.Snapshot.Rules);

        var record = (await h.Store.GetFirewallAccessAsync(id))!;
        h.Policy.Snapshot = h.Policy.Snapshot with { Rules = [Harness.Exact(record.ToTestPlan()) with { Enabled = false }] };
        var disabled = await h.Coordinator.SynchronizeAsync(id, CancellationToken.None);
        Assert.Equal(FirewallAccessPhase.Stale, disabled.Phase);
    }

    [Fact]
    public async Task Externally_deleted_rule_is_recreated_only_after_an_explicit_repair()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var created = await h.CreateExactAsync(id);
        h.Policy.Snapshot = h.Policy.Snapshot with { Rules = [] };

        var observed = await h.Coordinator.SynchronizeAsync(id, CancellationToken.None);
        Assert.Equal(FirewallAccessPhase.NeedsAttention, observed.Phase);
        Assert.Empty(h.Policy.Snapshot.Rules);

        var repair = await h.Coordinator.PrepareAsync(
            id, FirewallHelperOperation.Create, false, CancellationToken.None);
        var command = Assert.IsType<FirewallHelperCommand>(
            FirewallHelperCommandParser.Parse(repair.Arguments).Command);
        Assert.True(repair.Ready);
        Assert.Equal(FirewallHelperOperation.Create, command.Operation);
        Assert.Equal(created.RuleId, command.RuleId);
        h.ApplyTicket(repair);

        var ready = await h.Coordinator.CompleteAsync(id, repair.OperationId, false,
            (int)FirewallHelperExitCode.Applied, "repaired", CancellationToken.None);
        Assert.Equal(FirewallAccessPhase.Configured, ready.Phase);
        Assert.Equal(created.RuleId, ready.RuleId);
    }

    [Fact]
    public async Task Externally_deleted_rule_is_not_recreated_over_a_foreign_covering_allow()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var created = await h.CreateExactAsync(id);
        var plan = h.TargetPlan(id, created.RuleId, 25566);
        var foreign = Harness.Exact(plan) with
        {
            Name = "Administrator Java allow", Grouping = "Administrator", Description = "foreign"
        };
        h.Policy.Snapshot = h.Policy.Snapshot with { Rules = [foreign] };

        var repair = await h.Coordinator.PrepareAsync(
            id, FirewallHelperOperation.Create, false, CancellationToken.None);

        Assert.False(repair.Ready);
        Assert.Same(foreign, h.Policy.Snapshot.Rules.Single());
    }

    [Fact]
    public async Task Remove_success_clears_ownership_and_failure_keeps_recovery_evidence()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        _ = await h.CreateExactAsync(id);

        var removal = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Remove, false, CancellationToken.None);
        var failed = await h.Coordinator.CompleteAsync(id, removal.OperationId, false,
            (int)FirewallHelperExitCode.UnexpectedFailure, "failed", CancellationToken.None);
        Assert.True(failed.RemovalPending);
        Assert.True((await h.Store.GetFirewallAccessAsync(id))!.Configured);

        var retry = await h.Coordinator.PrepareAsync(id, FirewallHelperOperation.Remove, false, CancellationToken.None);
        h.Policy.Snapshot = h.Policy.Snapshot with { Rules = [] };
        var removed = await h.Coordinator.CompleteAsync(id, retry.OperationId, false,
            (int)FirewallHelperExitCode.Applied, "removed", CancellationToken.None);
        Assert.False(removed.Configured);
        Assert.False(removed.RemovalPending);
        Assert.Equal(Guid.Empty, removed.RuleId);
    }

    [Fact]
    public async Task Owned_rule_removal_leaves_broad_foreign_allow_untouched()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var plan = h.TargetPlan(id, Guid.NewGuid(), 25566);
        var foreign = Harness.Exact(plan) with
        {
            Name = "Broad foreign allow",
            Grouping = "Other application",
            Description = "foreign",
            ApplicationName = "",
            LocalPorts = ""
        };
        h.Policy.Snapshot = h.Policy.Snapshot with { Rules = [foreign] };

        var created = await h.CreateExactAsync(id);
        Assert.Equal(FirewallAccessPhase.Configured, created.Phase);
        Assert.Equal(FirewallRuleOwner.ChunkPilot, created.Owner);
        Assert.Equal(foreign.Name, created.OtherAllowRuleName);
        Assert.Equal(2, h.Policy.Snapshot.Rules.Count);

        var removal = await h.Coordinator.PrepareAsync(
            id, FirewallHelperOperation.Remove, false, CancellationToken.None);
        h.Policy.Snapshot = h.Policy.Snapshot with { Rules = [foreign] };
        var removed = await h.Coordinator.CompleteAsync(id, removal.OperationId, false,
            (int)FirewallHelperExitCode.Applied, "removed owned rule", CancellationToken.None);

        Assert.False(removed.Configured);
        Assert.Same(foreign, h.Policy.Snapshot.Rules.Single());
    }

    [Fact]
    public async Task Server_deletion_is_refused_until_owned_rule_is_verified_gone()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        _ = await h.CreateExactAsync(id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Coordinator.EnsureDeletionSafeAsync(id, CancellationToken.None));
        Assert.NotNull(h.Supervisor.Get(id));

        h.Policy.Snapshot = h.Policy.Snapshot with { Rules = [] };
        await h.Coordinator.EnsureDeletionSafeAsync(id, CancellationToken.None);
        Assert.Null(await h.Store.GetFirewallAccessAsync(id));
    }

    [Fact]
    public async Task Server_deletion_is_refused_while_an_elevation_operation_is_pending()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddServerAsync(25566);
        var pending = await h.Coordinator.PrepareAsync(
            id, FirewallHelperOperation.Create, false, CancellationToken.None);

        Assert.True(pending.Ready);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Coordinator.EnsureDeletionSafeAsync(id, CancellationToken.None));
        Assert.NotNull(h.Supervisor.Get(id));
        Assert.Null(await h.Store.GetFirewallAccessAsync(id));
    }

    [Fact]
    public async Task Stopped_server_without_a_persisted_managed_runtime_is_not_trusted()
    {
        await using var h = await Harness.StartAsync(root);
        var id = await h.AddUnassignedServerAsync(25566);

        var state = await h.Coordinator.CheckAsync(id, CancellationToken.None);
        var ticket = await h.Coordinator.PrepareAsync(
            id, FirewallHelperOperation.Create, false, CancellationToken.None);

        Assert.Equal(FirewallAccessPhase.RuntimeUnavailable, state.Phase);
        Assert.Equal(FirewallAccessFailure.RuntimeUnavailable, state.Failure);
        Assert.Equal(25566, state.Port);
        Assert.Equal(FirewallProfile.Private, state.SelectedProfile);
        Assert.Equal("Fixture network", state.NetworkName);
        Assert.False(ticket.Ready);
        Assert.Null(await h.Store.GetFirewallAccessAsync(id));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private WindowsFirewallCoordinator coordinator;
        private Harness(ChunkPilotStore store, ServerSupervisor supervisor, MutablePolicy policy,
            MutableCategory category, WindowsFirewallTargetResolver resolver, WindowsFirewallCoordinator coordinator,
            string root)
        {
            Store = store;
            Supervisor = supervisor;
            Policy = policy;
            Category = category;
            Resolver = resolver;
            this.coordinator = coordinator;
            Root = root;
            Java1 = Path.GetFullPath(Path.Combine(root, "java-one", "bin", "java.exe"));
            Java2 = Path.GetFullPath(Path.Combine(root, "java-two", "bin", "java.exe"));
        }

        public string Root { get; }
        public string Java1 { get; }
        public string Java2 { get; }
        public ChunkPilotStore Store { get; }
        public ServerSupervisor Supervisor { get; }
        public MutablePolicy Policy { get; }
        public MutableCategory Category { get; }
        public WindowsFirewallTargetResolver Resolver { get; }
        public WindowsFirewallCoordinator Coordinator => coordinator;

        public static async Task<Harness> StartAsync(string root)
        {
            Directory.CreateDirectory(root);
            var paths = new AppDataPaths(root, Path.Combine(root, "servers"));
            var store = new ChunkPilotStore(paths);
            await store.InitializeAsync();
            var supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
                new MinecraftStatusClient(), new BackupService(paths, store), NullLoggerFactory.Instance);
            await supervisor.InitializeAsync();
            var policy = new MutablePolicy();
            var category = new MutableCategory();
            var resolver = new WindowsFirewallTargetResolver(new FixedNetwork(), category, _ => true);
            var coordinator = new WindowsFirewallCoordinator(store, supervisor, policy, resolver,
                NullLogger<WindowsFirewallCoordinator>.Instance);
            return new Harness(store, supervisor, policy, category, resolver, coordinator, root);
        }

        public async Task<Guid> AddServerAsync(int port)
        {
            var id = Guid.NewGuid();
            await UpdateServerAsync(id, port, Java1);
            return id;
        }

        public async Task<Guid> AddUnassignedServerAsync(int port)
        {
            var id = Guid.NewGuid();
            var serverRoot = Path.Combine(Root, "unassigned-" + id.ToString("N"));
            Directory.CreateDirectory(serverRoot);
            await Supervisor.ImportAsync(new ServerDefinition
            {
                Id = id,
                Name = "Unassigned firewall fixture",
                RootPath = serverRoot,
                WorkingDirectory = serverRoot,
                Executable = Java1,
                Port = port
            });
            return id;
        }

        public async Task UpdateServerAsync(Guid id, int port, string java)
        {
            var serverRoot = Path.Combine(Root, "server-" + id.ToString("N"));
            Directory.CreateDirectory(serverRoot);
            await Supervisor.ImportAsync(new ServerDefinition
            {
                Id = id,
                Name = "Firewall fixture",
                RootPath = serverRoot,
                WorkingDirectory = serverRoot,
                Executable = java,
                Port = port
            });
            var runtime = new ManagedJavaRuntime
            {
                Id = GuidUtility(java),
                JavaPath = java,
                InstallationRoot = Path.GetDirectoryName(Path.GetDirectoryName(java))!,
                Vendor = "Fixture",
                Version = "21",
                MajorVersion = 21,
                Architecture = "x64",
                IsManaged = true,
                Health = RuntimeHealth.Healthy
            };
            await Store.UpsertManagedJavaRuntimeAsync(runtime);
            await Store.SetJavaAssignmentAsync(id, runtime.Id, java, "Fixture managed runtime");
        }

        private static Guid GuidUtility(string value)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
            return new Guid(bytes.AsSpan(0, 16));
        }

        public FirewallRulePlan TargetPlan(Guid serverId, Guid ruleId, int port) => new()
        {
            ServerId = serverId,
            RuleId = ruleId,
            ProgramPath = Supervisor.Get(serverId).Definition.Executable,
            Port = port,
            Profiles = WindowsFirewallPolicy.ProfileFor(Category.Category),
            TargetLocalAddress = "192.168.1.50",
            TargetInterfaceName = "Ethernet",
            TargetInterfaceType = "Lan"
        };

        public async Task<WindowsFirewallState> CreateExactAsync(Guid id)
        {
            var ticket = await coordinator.PrepareAsync(id, FirewallHelperOperation.Create, false, CancellationToken.None);
            ApplyTicket(ticket);
            return await coordinator.CompleteAsync(id, ticket.OperationId, false,
                (int)FirewallHelperExitCode.Applied, "fixture", CancellationToken.None);
        }

        public void ApplyTicket(FirewallElevationTicket ticket)
        {
            var command = Assert.IsType<FirewallHelperCommand>(FirewallHelperCommandParser.Parse(ticket.Arguments).Command);
            var other = Policy.Snapshot.Rules.Where(rule => rule.Name != command.ToPlan().RuleName).ToArray();
            Policy.Snapshot = Policy.Snapshot with { Rules = [.. other, Exact(command.ToPlan())] };
        }

        public async Task RestartCoordinatorAsync()
        {
            await coordinator.DisposeAsync();
            coordinator = new WindowsFirewallCoordinator(Store, Supervisor, Policy, Resolver,
                NullLogger<WindowsFirewallCoordinator>.Instance);
            await coordinator.ReconcileAllAsync(CancellationToken.None);
        }

        public static FirewallRuleSnapshot Exact(FirewallRulePlan plan) => new()
        {
            Name = plan.RuleName,
            Description = plan.Description,
            Grouping = plan.Grouping,
            Enabled = true,
            Direction = FirewallRuleDirection.Inbound,
            Action = FirewallRuleAction.Allow,
            Protocol = WindowsFirewallPolicy.ProtocolTcp,
            LocalPorts = plan.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            RemotePorts = "*",
            LocalAddresses = "*",
            RemoteAddresses = "*",
            ApplicationName = plan.ProgramPath,
            Profiles = plan.Profiles,
            InterfaceTypes = "All"
        };

        public async ValueTask DisposeAsync()
        {
            await coordinator.DisposeAsync();
            await Supervisor.DisposeAsync();
            await Store.DisposeAsync();
        }

        private sealed class FixedNetwork : IRouterNetworkView
        {
            public IReadOnlyList<RouterGatewayCandidate> Enumerate() =>
            [
                new(new LanInterfaceCandidate(
                        "{11111111-1111-1111-1111-111111111111}", "Ethernet", "Fixture Ethernet",
                        NetworkInterfaceType.Ethernet, OperationalStatus.Up, 1_000_000_000,
                        true, true, [new LanAddressCandidate(IPAddress.Parse("192.168.1.50"), 24)], 16),
                    [IPAddress.Parse("192.168.1.1")])
            ];
        }
    }

    private sealed class MutablePolicy : IWindowsFirewallPolicyReader
    {
        public FirewallPolicySnapshot Snapshot { get; set; } = new()
        {
            Available = true,
            CurrentProfiles = FirewallProfile.Private,
            EnabledProfiles = FirewallProfile.Domain | FirewallProfile.Private | FirewallProfile.Public,
            ModifyState = FirewallPolicyModifyState.Ok,
            Rules = []
        };
        public FirewallPolicySnapshot Read() => Snapshot;
    }

    private sealed class MutableCategory : INetworkCategoryView
    {
        public bool Available { get; set; } = true;
        public NetworkListStatus Status { get; set; } = NetworkListStatus.Available;
        public WindowsNetworkCategory Category { get; set; } = WindowsNetworkCategory.Private;
        public IReadOnlyList<NetworkCategoryBinding> Enumerate() =>
        Available ?
        [
            new()
            {
                AdapterId = "{11111111-1111-1111-1111-111111111111}",
                InterfaceIndex = 16,
                NetworkName = "Fixture network",
                Category = Category,
                Connected = true
            }
        ] : [];

        public NetworkCategorySnapshot Read() => Status == NetworkListStatus.Available
            ? new NetworkCategorySnapshot { Bindings = Enumerate(), Detail = "Fixture NLM read." }
            : NetworkCategorySnapshot.Unavailable(Status, "Fixture NLM failure.");
    }
}

internal static class FirewallIntegrationTestExtensions
{
    public static FirewallRulePlan ToTestPlan(this FirewallAccessRecord record) => new()
    {
        ServerId = record.ServerId,
        RuleId = record.RuleId,
        ProgramPath = record.ProgramPath,
        Port = record.Port,
        Transport = record.Transport,
        Profiles = record.Profiles
    };
}
