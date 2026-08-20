using System.Collections.Concurrent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.Agent;

/// <summary>
/// Owns ChunkPilot's Windows Firewall access for every managed server: what the user configured, the
/// evidence that a rule is ChunkPilot's own, and the truth about what Windows currently reports.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here changes Windows Firewall. Every mutation happens in a separate one-shot elevated
/// helper, and only ever because the user asked for it: <see cref="PrepareAsync"/> is reachable from
/// one deliberate action, and no lifecycle event, reconciliation pass, refresh, connection test or
/// router result can reach it. Starting a server, stopping one, selecting Direct internet, opening
/// Overview and restarting the Agent all read, and only read.
/// </para>
/// <para>
/// The firewall rule and the router mapping are deliberately different layers. A router mapping is
/// public exposure and is bound to the running server, so it opens and closes with the lifecycle. A
/// firewall rule is a durable permission for one exact executable on one exact port, so it survives
/// stops, starts, restarts and application exit — and asking for administrator rights every time a
/// server started would be both useless and hostile.
/// </para>
/// </remarks>
public sealed class WindowsFirewallCoordinator : IAsyncDisposable
{
    /// <summary>
    /// How long a read of the whole rule set stays usable. Enumerating several hundred rules is not
    /// free, and the App polls; a deliberate action or a completed mutation always reads afresh.
    /// </summary>
    private static readonly TimeSpan PolicyCacheLifetime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a prepared operation may wait for the elevation prompt before its result is refused.
    /// The user is not hurried by it; it only bounds how stale an accepted result can be.
    /// </summary>
    private static readonly TimeSpan ElevationLifetime = TimeSpan.FromMinutes(10);

    private readonly ChunkPilotStore store;
    private readonly ServerSupervisor supervisor;
    private readonly IWindowsFirewallPolicyReader firewall;
    private readonly WindowsFirewallTargetResolver targets;
    private readonly ILogger<WindowsFirewallCoordinator> logger;
    private readonly ConcurrentDictionary<Guid, ServerRuntime> runtimes = new();
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim policyGate = new(1, 1);
    private FirewallPolicySnapshot? cachedPolicy;
    private DateTimeOffset cachedPolicyAt;

    public WindowsFirewallCoordinator(
        ChunkPilotStore store,
        ServerSupervisor supervisor,
        IWindowsFirewallPolicyReader firewall,
        WindowsFirewallTargetResolver targets,
        ILogger<WindowsFirewallCoordinator> logger,
        TimeProvider? clock = null)
    {
        this.store = store;
        this.supervisor = supervisor;
        this.firewall = firewall;
        this.targets = targets;
        this.logger = logger;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Reads the current truth. Changes nothing, and never raises an elevation prompt.</summary>
    public Task<WindowsFirewallState> GetStateAsync(Guid serverId, CancellationToken cancellationToken) =>
        EvaluateAsync(serverId, forceRefresh: false, recordCheck: false, cancellationToken);

    /// <summary>The user asked for a fresh look. Still read-only.</summary>
    public Task<WindowsFirewallState> CheckAsync(Guid serverId, CancellationToken cancellationToken) =>
        EvaluateAsync(serverId, forceRefresh: true, recordCheck: true, cancellationToken);

    /// <summary>
    /// Reconciles one server against what Windows Firewall actually says. Reads only: an administrator
    /// who deleted, disabled or edited ChunkPilot's rule is not argued with, they are reported.
    /// </summary>
    public Task<WindowsFirewallState> SynchronizeAsync(Guid serverId, CancellationToken cancellationToken) =>
        EvaluateAsync(serverId, forceRefresh: true, recordCheck: true, cancellationToken);

    /// <summary>
    /// Prepares one privileged operation the user has explicitly asked for, and returns the exact
    /// arguments the helper must be given.
    /// </summary>
    /// <remarks>
    /// The arguments are derived here, from authoritative state, and validated here. The App never
    /// composes them. Preparing does not change anything: if the user dismisses the prompt, nothing
    /// happened and nothing was recorded.
    /// </remarks>
    public async Task<FirewallElevationTicket> PrepareAsync(
        Guid serverId,
        FirewallHelperOperation operation,
        bool publicApproved,
        CancellationToken cancellationToken)
    {
        var runtime = Runtime(serverId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await store.GetFirewallAccessAsync(serverId, cancellationToken).ConfigureAwait(false);
            runtime.TransientFailure = FirewallAccessFailure.None;
            runtime.TransientDetail = "";
            var policy = await ReadPolicyAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            var target = await ResolveTargetAsync(serverId, cancellationToken).ConfigureAwait(false);

            if (runtime.Pending is { } active && !active.HasExpired(clock.GetUtcNow()))
            {
                return new FirewallElevationTicket
                {
                    Ready = false,
                    OperationId = active.Command.OperationId,
                    Operation = active.Command.Operation,
                    Detail = "A Windows Firewall administrator prompt is already in progress for this server.",
                    State = Project(serverId, record, policy, target, runtime)
                };
            }
            if (runtime.Pending is not null)
            {
                runtime.Pending = null;
                runtime.LivePhase = null;
            }

            if (operation == FirewallHelperOperation.Remove)
                return PrepareRemoval(serverId, runtime, record, policy, target);

            if (!policy.Available)
                return Refuse(serverId, runtime, record, policy, target,
                    "Windows Firewall did not answer, so nothing was attempted.");
            if (!target.Resolved)
                return Refuse(serverId, runtime, record, policy, target, target.Detail);
            if (!policy.HasCompleteMutationEvidence(target.Profile))
                return Refuse(serverId, runtime, record, policy, target,
                    "Windows Firewall policy could not be fully verified for this network, so nothing was attempted.");
            if (policy.ModifyState != FirewallPolicyModifyState.Ok ||
                policy.BlockAllInboundProfiles.HasFlag(target.Profile))
                return Refuse(serverId, runtime, record, policy, target,
                    "Windows says a local firewall rule would not take effect on this computer.");
            if (!policy.CurrentProfiles.HasFlag(target.Profile))
                return Refuse(serverId, runtime, record, policy, target,
                    "Windows does not report this network's selected firewall profile as active, so nothing was attempted.");
            if (!policy.EnabledProfiles.HasFlag(target.Profile))
                return Refuse(serverId, runtime, record, policy, target,
                    "Windows Firewall is switched off for this network, so ChunkPilot has nothing to configure.");

            var preflightPlan = new FirewallRulePlan
            {
                ServerId = serverId,
                RuleId = record?.RuleId ?? Guid.Empty,
                ProgramPath = target.ProgramPath,
                Port = target.Port,
                Transport = target.Transport,
                Profiles = target.Profile,
                TargetLocalAddress = target.LocalAddress,
                TargetInterfaceName = target.InterfaceName,
                TargetInterfaceType = target.InterfaceType
            };
            if (WindowsFirewallPolicy.FindApplicableBlockRule(preflightPlan, policy.Rules) is { } block)
                return Refuse(serverId, runtime, record, policy, target,
                    $"The existing Windows Firewall block rule '{block.Name}' applies to this server. It was left unchanged.");
            if (WindowsFirewallPolicy.FindPotentiallyApplicableUnknownBlockRule(preflightPlan, policy.Rules)
                is { } uncertainBlock)
                return Refuse(serverId, runtime, record, policy, target,
                    $"The existing Windows Firewall block rule '{uncertainBlock.Name}' has conditions ChunkPilot cannot verify. Nothing was changed.");
            if (WindowsFirewallPolicy.FindCoveringAllowRule(
                    preflightPlan, policy.Rules, record?.RuleId ?? Guid.Empty) is { } exactAllow)
                return Refuse(serverId, runtime, record, policy, target,
                    $"The existing Windows Firewall rule '{exactAllow.Name}' already exactly covers this server and was left unchanged.");
            if (target.Profile == FirewallProfile.Public && !publicApproved && !(record?.PublicApproved ?? false))
                return Refuse(serverId, runtime, record, policy, target,
                    "This network is classified Public. A Public exception has to be approved separately.");

            var ruleId = record is { Configured: true } && record.RuleId != Guid.Empty
                ? record.RuleId
                : Guid.Empty;
            var effectiveOperation = ruleId == Guid.Empty
                ? FirewallHelperOperation.Create
                : FirewallHelperOperation.Update;
            if (effectiveOperation == FirewallHelperOperation.Update)
            {
                var existingOwned = WindowsFirewallPolicy.FindByName(record!.RuleName, policy.Rules);
                if (existingOwned is null)
                {
                    // An administrator may deliberately delete an owned rule. A later explicit Repair
                    // may restore the same stable identity, but only after an authoritative enumeration
                    // proves the name is absent and no foreign allow already makes another rule needless.
                    var recoveryPlan = new FirewallRulePlan
                    {
                        ServerId = serverId,
                        RuleId = ruleId,
                        ProgramPath = target.ProgramPath,
                        Port = target.Port,
                        Transport = target.Transport,
                        Profiles = target.Profile,
                        TargetLocalAddress = target.LocalAddress,
                        TargetInterfaceName = target.InterfaceName,
                        TargetInterfaceType = target.InterfaceType
                    };
                    if (WindowsFirewallPolicy.FindCoveringAllowRule(
                            recoveryPlan, policy.Rules, record.RuleId) is not null)
                        return Refuse(serverId, runtime, record, policy, target,
                            "A Windows rule ChunkPilot does not own already covers this server. It was left unchanged.");
                    effectiveOperation = FirewallHelperOperation.Create;
                }
                else if (!WindowsFirewallPolicy.ProvesOwnership(record, existingOwned))
                    return Refuse(serverId, runtime, record, policy, target,
                        "The rule to update no longer carries ChunkPilot ownership evidence. Nothing was changed.");
            }

            var plan = new FirewallRulePlan
            {
                ServerId = serverId,
                RuleId = ruleId == Guid.Empty ? Guid.NewGuid() : ruleId,
                ProgramPath = target.ProgramPath,
                Port = target.Port,
                Transport = target.Transport,
                Profiles = target.Profile,
                TargetLocalAddress = target.LocalAddress,
                TargetInterfaceName = target.InterfaceName,
                TargetInterfaceType = target.InterfaceType
            };

            var collision = WindowsFirewallPolicy.FindByName(plan.RuleName, policy.Rules);
            if (collision is not null &&
                (record is null || !WindowsFirewallPolicy.ProvesOwnership(record, collision)))
                return Refuse(serverId, runtime, record, policy, target,
                    "A Windows Firewall rule already uses this identifier without ChunkPilot ownership evidence. Nothing was changed.");

            var command = new FirewallHelperCommand
            {
                Operation = effectiveOperation,
                OperationId = Guid.NewGuid(),
                ServerId = serverId,
                RuleId = plan.RuleId,
                ProgramPath = plan.ProgramPath,
                Port = plan.Port,
                Transport = plan.Transport,
                Profiles = plan.Profiles
            };
            var arguments = FirewallHelperCommandParser.ToArguments(command);

            // The Agent validates its own output before anything can be elevated with it. A command the
            // helper's own parser would refuse never reaches an administrator prompt.
            var rehearsal = FirewallHelperCommandParser.Parse(arguments);
            if (rehearsal.Command is null)
                return Refuse(serverId, runtime, record, policy, target,
                    $"ChunkPilot refused its own firewall request: {rehearsal.Error}");

            runtime.Pending = new PendingElevation(command, plan, target, publicApproved, clock.GetUtcNow());
            runtime.LivePhase = FirewallAccessPhase.WaitingForElevation;
            return new FirewallElevationTicket
            {
                Ready = true,
                OperationId = command.OperationId,
                Operation = effectiveOperation,
                Arguments = arguments,
                Detail = target.Detail,
                State = Project(serverId, record, policy, target, runtime)
            };
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    /// <summary>
    /// Accepts, or refuses, the result of one privileged run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The helper's exit code is never treated as proof. Windows Firewall is read again and every
    /// property of the rule is checked against the plan; only then is anything projected as configured.
    /// </para>
    /// <para>
    /// A result whose operation is not the one still in flight is discarded rather than applied, and a
    /// result whose target no longer matches the server — a port or a runtime changed while the prompt
    /// was open — is never recorded as a success for the server it no longer describes.
    /// </para>
    /// </remarks>
    public async Task<WindowsFirewallState> CompleteAsync(
        Guid serverId,
        Guid operationId,
        bool cancelled,
        int exitCode,
        string launcherDetail,
        CancellationToken cancellationToken)
        => await CompleteAsync(serverId, operationId, cancelled, exitCode, launcherDetail,
            FirewallElevationFailure.None, cancellationToken).ConfigureAwait(false);

    public async Task<WindowsFirewallState> CompleteAsync(
        Guid serverId,
        Guid operationId,
        bool cancelled,
        int exitCode,
        string launcherDetail,
        FirewallElevationFailure elevationFailure,
        CancellationToken cancellationToken)
    {
        var runtime = Runtime(serverId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = runtime.Pending;
            if (pending is not null && pending.HasExpired(clock.GetUtcNow()))
            {
                // An administrator prompt nobody ever answered. Abandoning it releases the surface
                // rather than leaving a server permanently busy.
                runtime.Pending = null;
                runtime.LivePhase = null;
                pending = null;
            }
            if (pending is null || pending.Command.OperationId != operationId)
            {
                // A result from an operation that has already been superseded, cancelled or abandoned.
                // Discarding it is the whole point of carrying an operation identity.
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "A firewall result for {ServerId} arrived for an operation that is no longer current.",
                        serverId);
                return await EvaluateLockedAsync(serverId, runtime, forceRefresh: false, recordCheck: false,
                    cancellationToken).ConfigureAwait(false);
            }

            runtime.Pending = null;
            runtime.LivePhase = null;
            var record = await store.GetFirewallAccessAsync(serverId, cancellationToken).ConfigureAwait(false);

            if (cancelled)
            {
                // Cancellation creates no durable intent. The current in-memory snapshot still explains
                // the outcome, and an explicit read-only check replaces it.
                runtime.TransientFailure = FirewallAccessFailure.Cancelled;
                runtime.TransientDetail = launcherDetail.Length > 0
                    ? launcherDetail
                    : "The Windows administrator prompt was dismissed.";
                var cancelledPolicy = await ReadPolicyAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
                var cancelledTarget = await ResolveTargetAsync(serverId, cancellationToken).ConfigureAwait(false);
                return Project(serverId, record, cancelledPolicy, cancelledTarget, runtime);
            }

            var target = await ResolveTargetAsync(serverId, cancellationToken).ConfigureAwait(false);
            var policy = await ReadPolicyAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            runtime.TransientFailure = FirewallAccessFailure.None;
            runtime.TransientDetail = "";
            var code = Enum.IsDefined((FirewallHelperExitCode)exitCode)
                ? (FirewallHelperExitCode)exitCode
                : FirewallHelperExitCode.UnexpectedFailure;

            var updated = elevationFailure != FirewallElevationFailure.None
                ? ApplyElevationFailure(pending, record, elevationFailure, launcherDetail, now)
                : pending.Command.Operation == FirewallHelperOperation.Remove
                ? ApplyRemoval(pending, record, policy, code, launcherDetail, now)
                : ApplyCreation(pending, record, policy, target, code, launcherDetail, now);

            await store.UpsertFirewallAccessAsync(updated, CancellationToken.None).ConfigureAwait(false);
            return Project(serverId, updated, policy, target, runtime);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    /// <summary>Abandons a prepared operation whose prompt was never answered.</summary>
    public async Task<WindowsFirewallState> CancelAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var runtime = Runtime(serverId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            runtime.Pending = null;
            runtime.LivePhase = null;
            return await EvaluateLockedAsync(serverId, runtime, forceRefresh: false, recordCheck: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    /// <summary>
    /// Deals with a server being deleted while ChunkPilot still owns a firewall rule for it.
    /// </summary>
    /// <remarks>
    /// A rule can only be withdrawn under an administrator prompt, and the Agent has no user in front
    /// of it. So the ownership evidence is kept rather than dropped — the rule is never silently
    /// orphaned — and the deletion says exactly which rule was left and how to withdraw it.
    /// </remarks>
    public async Task<string> PrepareForDeletionAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var record = await store.GetFirewallAccessAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return "";
        if (!record.Configured)
        {
            await store.DeleteFirewallAccessAsync(serverId, cancellationToken).ConfigureAwait(false);
            return "";
        }
        var policy = await ReadPolicyAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        var existing = WindowsFirewallPolicy.FindByName(record.RuleName, policy.Rules);
        if (policy.RulesAvailable && existing is null)
        {
            // The rule is already gone, so there is nothing left to own.
            await store.DeleteFirewallAccessAsync(serverId, cancellationToken).ConfigureAwait(false);
            return "";
        }
        await store.UpsertFirewallAccessAsync(record with
        {
            ServerRemoved = true,
            RemovalPending = true,
            LastFailure = FirewallAccessFailure.RemovalFailed,
            LastCheckedAt = clock.GetUtcNow(),
            LastOperationDetail =
                "The server was deleted while this rule was still configured. Removing it needs administrator " +
                "approval, so the evidence for it is retained."
        }, cancellationToken).ConfigureAwait(false);
        return $" ChunkPilot's Windows Firewall rule \"{record.RuleName}\" was left in place, because removing " +
               "it needs administrator approval. You can delete it in Windows Defender Firewall.";
    }

    /// <summary>
    /// Refuses deletion while a proven-owned firewall rule still needs an administrator-approved
    /// removal. This preserves the server entry as the recovery surface instead of silently orphaning
    /// an operating-system permission or risking any server files.
    /// </summary>
    public async Task EnsureDeletionSafeAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var runtime = Runtime(serverId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runtime.Pending is not null)
                throw new InvalidOperationException(
                    "Finish or cancel the Windows Firewall administrator prompt before removing this server.");
            var record = await store.GetFirewallAccessAsync(serverId, cancellationToken).ConfigureAwait(false);
            if (record is null || !record.Configured && !record.RemovalPending)
                return;
            var policy = await ReadPolicyAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            var existing = WindowsFirewallPolicy.FindByName(record.RuleName, policy.Rules);
            if (policy.RulesAvailable && existing is null)
            {
                await store.DeleteFirewallAccessAsync(serverId, cancellationToken).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException(
                "Remove this server's Windows Firewall access first. ChunkPilot keeps the server entry until " +
                "Windows confirms its owned rule is gone.");
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    /// <summary>
    /// Reads the firewall once for every server that has ever been configured, so a rule an
    /// administrator changed outside ChunkPilot is noticed when ChunkPilot comes back. Reads only.
    /// </summary>
    public async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FirewallAccessRecord> records;
        try
        {
            records = await store.GetFirewallAccessRecordsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Windows Firewall records could not be read.");
            return;
        }
        var known = supervisor.Definitions.Select(definition => definition.Id).ToHashSet();
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!known.Contains(record.ServerId))
                continue;
            try
            {
                _ = await SynchronizeAsync(record.ServerId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "Windows Firewall reconciliation failed for {ServerId}.", record.ServerId);
            }
        }
    }

    // ── Evaluation ───────────────────────────────────────────────────────────────────────────────

    private async Task<WindowsFirewallState> EvaluateAsync(
        Guid serverId, bool forceRefresh, bool recordCheck, CancellationToken cancellationToken)
    {
        var runtime = Runtime(serverId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EvaluateLockedAsync(serverId, runtime, forceRefresh, recordCheck, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private async Task<WindowsFirewallState> EvaluateLockedAsync(
        Guid serverId, ServerRuntime runtime, bool forceRefresh, bool recordCheck,
        CancellationToken cancellationToken)
    {
        var record = await store.GetFirewallAccessAsync(serverId, cancellationToken).ConfigureAwait(false);
        var policy = await ReadPolicyAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        var target = await ResolveTargetAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (recordCheck)
        {
            runtime.TransientFailure = FirewallAccessFailure.None;
            runtime.TransientDetail = "";
        }
        if (recordCheck && record is not null)
        {
            record = record with { LastCheckedAt = clock.GetUtcNow() };
            await store.UpsertFirewallAccessAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        return Project(serverId, record, policy, target, runtime);
    }

    /// <summary>
    /// Turns durable evidence and live Windows state into the one state the App renders.
    /// </summary>
    /// <remarks>
    /// The order is the order of the questions that actually matter. Whether the machine can answer at
    /// all comes first, then whether a change would take effect, then whether something is already
    /// blocked, then what ChunkPilot owns, and only last what could be offered.
    /// </remarks>
    private WindowsFirewallState Project(
        Guid serverId,
        FirewallAccessRecord? record,
        FirewallPolicySnapshot policy,
        FirewallTargetResolution target,
        ServerRuntime runtime)
    {
        var plan = PlanFor(serverId, record, target);
        var owned = record is not null && plan is not null
            ? WindowsFirewallPolicy.FindByName(record.RuleName, policy.Rules)
            : null;
        var provenOwned = record is not null && owned is not null &&
                          WindowsFirewallPolicy.ProvesOwnership(record, owned);
        var staleReasons = provenOwned && plan is not null
            ? WindowsFirewallPolicy.Differences(plan, owned!)
            : [];
        var covering = plan is not null
            ? WindowsFirewallPolicy.FindCoveringAllowRule(plan, policy.Rules, record?.RuleId ?? Guid.Empty)
            : null;
        var otherAllow = plan is not null
            ? WindowsFirewallPolicy.FindOtherForeignAllowRule(plan, policy.Rules, record?.RuleId ?? Guid.Empty)
            : null;
        var blocking = plan is not null
            ? WindowsFirewallPolicy.FindApplicableBlockRule(plan, policy.Rules)
            : null;
        var unknownBlocking = blocking is null && plan is not null
            ? WindowsFirewallPolicy.FindPotentiallyApplicableUnknownBlockRule(plan, policy.Rules)
            : null;

        var state = new WindowsFirewallState
        {
            ServerId = serverId,
            Busy = runtime.LivePhase is not null,
            OperationId = runtime.Pending?.Command.OperationId ?? Guid.Empty,
            Configured = record?.Configured ?? false,
            RemovalPending = record?.RemovalPending ?? false,
            RuleId = record?.RuleId ?? Guid.Empty,
            RuleName = record?.RuleName ?? "",
            RuleGroup = WindowsFirewallPolicy.RuleGroup,
            ProgramPath = target.ProgramPath.Length > 0 ? target.ProgramPath : record?.ProgramPath ?? "",
            JavaVerified = target.ProgramPath.Length > 0,
            RuntimeSource = target.RuntimeSource,
            Port = target.Port > 0 ? target.Port : record?.Port ?? 0,
            PortVerified = target.Port > 0,
            Transport = MappingTransport.Tcp,
            Profiles = provenOwned ? owned!.Profiles : record?.Profiles ?? target.Profile,
            SelectedProfile = target.Profile,
            NetworkProfileVerified = target.Profile != FirewallProfile.None,
            Category = target.Category,
            NetworkName = target.NetworkName,
            InterfaceName = target.InterfaceName,
            InterfaceIndex = target.InterfaceIndex,
            LocalAddress = target.LocalAddress,
            GatewayAddress = target.GatewayAddress,
            NetworkPathStatus = target.NetworkPathStatus,
            NetworkListStatus = target.NetworkListStatus,
            TargetProblem = target.Problem,
            FirewallApiAvailable = policy.Available,
            FirewallPlatformStatus = policy.PlatformStatus,
            FirewallPolicyUnavailableFields = policy.UnavailableFieldsFor(target.Profile),
            FirewallEnabledForProfile =
                target.Profile != FirewallProfile.None && policy.EnabledProfiles.HasFlag(target.Profile),
            BlockAllInboundForProfile =
                target.Profile != FirewallProfile.None && policy.BlockAllInboundProfiles.HasFlag(target.Profile),
            ModifyState = policy.ModifyState,
            ExistingRuleName = covering?.Name ?? "",
            ExistingRuleCoverage = covering is null
                ? FirewallRuleCoverage.None
                : FirewallRuleCoverage.ExactEquivalent,
            OtherAllowRuleName = otherAllow?.Rule.Name ?? "",
            OtherAllowRuleCoverage = otherAllow?.Coverage ?? FirewallRuleCoverage.None,
            BlockingRuleName = blocking?.Name ?? unknownBlocking?.Name ?? "",
            BlockingRuleCoverage = blocking is not null && plan is not null
                ? WindowsFirewallPolicy.EvaluateRuleCoverage(plan, blocking).Coverage
                : unknownBlocking is not null ? FirewallRuleCoverage.UnknownOrUnsupported
                : FirewallRuleCoverage.None,
            StaleReasons = staleReasons,
            ConfiguredAt = record?.ConfiguredAt,
            LastCheckedAt = record?.LastCheckedAt,
            TargetDetail = target.Detail,
            NetworkListDetail = target.NetworkListDetail,
            FirewallPolicyDetail = policy.Detail,
            LastOperationDetail = runtime.TransientDetail.Length > 0
                ? runtime.TransientDetail
                : record?.LastOperationDetail ?? "",
            PublicApproved = record?.PublicApproved ?? false,
            Failure = target.Problem switch
            {
                FirewallTargetProblem.RuntimeUnavailable => FirewallAccessFailure.RuntimeUnavailable,
                FirewallTargetProblem.PortUnavailable => FirewallAccessFailure.PortUnavailable,
                FirewallTargetProblem.NetworkPathUnavailable or FirewallTargetProblem.NetworkPathAmbiguous or
                    FirewallTargetProblem.NetworkListUnavailable or FirewallTargetProblem.NetworkProfileUnavailable or
                    FirewallTargetProblem.NetworkProfileAmbiguous => FirewallAccessFailure.NetworkUnavailable,
                _ => runtime.TransientFailure != FirewallAccessFailure.None
                    ? runtime.TransientFailure
                    : record?.LastFailure ?? FirewallAccessFailure.None
            }
        };

        var phase = ResolvePhase(record, policy, target, runtime, owned, provenOwned, staleReasons,
            covering, blocking, unknownBlocking, out var owner);
        var projected = state with
        {
            Phase = phase,
            Owner = owner,
            // A settled good state reports no failure, whatever an abandoned earlier attempt left behind.
            Failure = phase is FirewallAccessPhase.Configured or FirewallAccessPhase.ExistingWindowsRule
                ? FirewallAccessFailure.None
                : state.Failure
        };
        return projected with { Diagnostic = WindowsFirewallDiagnostics.Evaluate(projected) };
    }

    private static FirewallAccessPhase ResolvePhase(
        FirewallAccessRecord? record,
        FirewallPolicySnapshot policy,
        FirewallTargetResolution target,
        ServerRuntime runtime,
        FirewallRuleSnapshot? owned,
        bool provenOwned,
        IReadOnlyList<string> staleReasons,
        FirewallRuleSnapshot? covering,
        FirewallRuleSnapshot? blocking,
        FirewallRuleSnapshot? unknownBlocking,
        out FirewallRuleOwner owner)
    {
        owner = provenOwned ? FirewallRuleOwner.ChunkPilot
            : covering is not null ? FirewallRuleOwner.ExistingWindowsRule
            : FirewallRuleOwner.None;

        if (runtime.LivePhase is { } live)
            return live;
        if (!policy.Available)
            return FirewallAccessPhase.Unsupported;
        if (record is { RemovalPending: true })
            return FirewallAccessPhase.NeedsAttention;
        if (owned is not null && !provenOwned)
            return FirewallAccessPhase.OwnershipConflict;
        if (policy.ModifyState is FirewallPolicyModifyState.GroupPolicyOverride
            or FirewallPolicyModifyState.InboundBlocked ||
            target.Profile != FirewallProfile.None && policy.BlockAllInboundProfiles.HasFlag(target.Profile))
            return FirewallAccessPhase.ManagedByOrganization;
        if (policy.UnavailableFieldsFor(target.Profile) != FirewallPolicyUnavailableFields.None)
            return FirewallAccessPhase.NeedsAttention;
        if (policy.ModifyState != FirewallPolicyModifyState.Ok)
            return FirewallAccessPhase.NeedsAttention;
        if (!target.Resolved)
            return target.Problem switch
            {
                FirewallTargetProblem.RuntimeUnavailable => FirewallAccessPhase.RuntimeUnavailable,
                FirewallTargetProblem.PortUnavailable => FirewallAccessPhase.PortUnavailable,
                FirewallTargetProblem.NetworkPathUnavailable or FirewallTargetProblem.NetworkPathAmbiguous or
                    FirewallTargetProblem.NetworkListUnavailable or FirewallTargetProblem.NetworkProfileUnavailable or
                    FirewallTargetProblem.NetworkProfileAmbiguous => FirewallAccessPhase.NetworkUnavailable,
                _ => FirewallAccessPhase.NeedsAttention
            };
        if (!policy.CurrentProfiles.HasFlag(target.Profile))
            return FirewallAccessPhase.NeedsAttention;
        if (!policy.EnabledProfiles.HasFlag(target.Profile))
            return FirewallAccessPhase.FirewallDisabled;
        if (blocking is not null)
            return FirewallAccessPhase.BlockedByPolicy;
        if (unknownBlocking is not null)
            return FirewallAccessPhase.NeedsAttention;
        if (record is { Configured: true })
        {
            if (owned is null)
                // The rule ChunkPilot created is not there any more. Somebody removed it outside
                // ChunkPilot, which is their right; ChunkPilot reports it rather than putting it back.
                return FirewallAccessPhase.NeedsAttention;
            return staleReasons.Count == 0 ? FirewallAccessPhase.Configured : FirewallAccessPhase.Stale;
        }
        if (covering is not null)
            return FirewallAccessPhase.ExistingWindowsRule;
        if (target.Profile == FirewallProfile.Public && !(record?.PublicApproved ?? false))
            return FirewallAccessPhase.PublicNetworkConfirmationRequired;
        if (record is { LastFailure: not FirewallAccessFailure.None and not FirewallAccessFailure.Cancelled })
            return FirewallAccessPhase.NeedsAttention;
        return FirewallAccessPhase.NeedsPermission;
    }

    private static FirewallRulePlan? PlanFor(
        Guid serverId, FirewallAccessRecord? record, FirewallTargetResolution target)
    {
        if (target.Resolved)
            return new FirewallRulePlan
            {
                ServerId = serverId,
                RuleId = record?.RuleId ?? Guid.Empty,
                ProgramPath = target.ProgramPath,
                Port = target.Port,
                Transport = target.Transport,
                Profiles = target.Profile,
                TargetLocalAddress = target.LocalAddress,
                TargetInterfaceName = target.InterfaceName,
                TargetInterfaceType = target.InterfaceType
            };
        if (record is null || record.RuleId == Guid.Empty)
            return null;
        // The machine cannot answer right now, but ChunkPilot still knows exactly what it created, so
        // the rule can still be found and reported.
        return new FirewallRulePlan
        {
            ServerId = serverId,
            RuleId = record.RuleId,
            ProgramPath = record.ProgramPath,
            Port = record.Port,
            Transport = record.Transport,
            Profiles = record.Profiles
        };
    }

    // ── Applying a privileged result ─────────────────────────────────────────────────────────────

    private static FirewallAccessRecord ApplyElevationFailure(
        PendingElevation pending,
        FirewallAccessRecord? record,
        FirewallElevationFailure failure,
        string detail,
        DateTimeOffset now)
    {
        var previous = record ?? new FirewallAccessRecord { ServerId = pending.Command.ServerId };
        return previous with
        {
            LastCheckedAt = now,
            LastFailure = failure switch
            {
                FirewallElevationFailure.HelperUnavailable => FirewallAccessFailure.HelperUnavailable,
                FirewallElevationFailure.PermissionDenied => FirewallAccessFailure.ElevationFailed,
                _ => FirewallAccessFailure.HelperUnavailable
            },
            LastOperationDetail = detail
        };
    }

    private FirewallAccessRecord ApplyCreation(
        PendingElevation pending,
        FirewallAccessRecord? record,
        FirewallPolicySnapshot policy,
        FirewallTargetResolution target,
        FirewallHelperExitCode code,
        string launcherDetail,
        DateTimeOffset now)
    {
        var previous = record ?? new FirewallAccessRecord { ServerId = pending.Command.ServerId };
        var found = WindowsFirewallPolicy.FindByName(pending.Plan.RuleName, policy.Rules);
        var carriesEvidence = found is not null &&
                              WindowsFirewallPolicy.CarriesOwnershipEvidence(found, pending.Plan.RuleId);
        var differences = carriesEvidence
            ? WindowsFirewallPolicy.Differences(pending.Plan, found!)
            : [];

        if (!carriesEvidence || differences.Count > 0)
        {
            // Whatever the helper reported, Windows does not show the rule ChunkPilot asked for. The
            // failure is recorded and nothing is claimed.
            return previous with
            {
                LastCheckedAt = now,
                LastFailure = FailureFor(code, found is null
                    ? FirewallAccessFailure.VerificationFailed
                    : FirewallAccessFailure.OwnershipConflict),
                LastOperationDetail = Detail(code, launcherDetail, found is null
                    ? "Windows Firewall does not show the rule ChunkPilot asked for."
                    : $"The rule Windows shows differs: {string.Join("; ", differences)}.")
            };
        }

        // The rule is right. Whether it describes the server it was prepared for is a separate
        // question, and one that a long-open elevation prompt can genuinely change.
        var targetChanged = !target.Resolved ||
                            !WindowsFirewallPolicy.SamePath(target.ProgramPath, pending.Plan.ProgramPath) ||
                            target.Port != pending.Plan.Port ||
                            target.Profile != pending.Plan.Profiles;

        return previous with
        {
            Configured = true,
            RuleId = pending.Plan.RuleId,
            RuleName = pending.Plan.RuleName,
            ProgramPath = pending.Plan.ProgramPath,
            Port = pending.Plan.Port,
            Transport = pending.Plan.Transport,
            Profiles = pending.Plan.Profiles,
            PublicApproved = pending.Plan.Profiles.HasFlag(FirewallProfile.Public) &&
                             (pending.PublicApproved || previous.PublicApproved),
            PublicApprovedAt = pending.Plan.Profiles.HasFlag(FirewallProfile.Public)
                ? previous.PublicApprovedAt ?? now
                : previous.PublicApprovedAt,
            ConfiguredAt = now,
            RemovalPending = false,
            ServerRemoved = false,
            LastCheckedAt = now,
            LastFailure = targetChanged ? FirewallAccessFailure.TargetChanged : FirewallAccessFailure.None,
            LastOperationDetail = targetChanged
                ? Detail(code, launcherDetail,
                    "This server changed while the administrator prompt was open, so the rule that was created " +
                    "no longer matches it. Update firewall access to bring them back into line.")
                : Detail(code, launcherDetail,
                    $"Windows Firewall shows {pending.Plan.RuleName} allowing " +
                    $"{WindowsFirewallPolicy.TransportName(pending.Plan.Transport)} {pending.Plan.Port} for " +
                    $"{Path.GetFileName(pending.Plan.ProgramPath)} on " +
                    $"{WindowsFirewallPolicy.DescribeProfiles(pending.Plan.Profiles)}.")
        };
    }

    private static FirewallAccessRecord ApplyRemoval(
        PendingElevation pending,
        FirewallAccessRecord? record,
        FirewallPolicySnapshot policy,
        FirewallHelperExitCode code,
        string launcherDetail,
        DateTimeOffset now)
    {
        var previous = record ?? new FirewallAccessRecord { ServerId = pending.Command.ServerId };
        var stillThere = WindowsFirewallPolicy.FindByName(previous.RuleName, policy.Rules);
        if (policy.RulesAvailable && stillThere is null)
            return previous with
            {
                Configured = false,
                RemovalPending = false,
                RuleId = Guid.Empty,
                RuleName = "",
                ConfiguredAt = null,
                PublicApproved = false,
                PublicApprovedAt = null,
                LastCheckedAt = now,
                LastFailure = FirewallAccessFailure.None,
                LastOperationDetail = Detail(code, launcherDetail,
                    "Windows Firewall no longer lists ChunkPilot's rule for this server.")
            };

        return previous with
        {
            RemovalPending = true,
            LastCheckedAt = now,
            LastFailure = FirewallAccessFailure.RemovalFailed,
            LastOperationDetail = Detail(code, launcherDetail,
                "ChunkPilot's rule is still listed in Windows Firewall, so the removal is remembered and can " +
                "be retried.")
        };
    }

    private FirewallElevationTicket PrepareRemoval(
        Guid serverId,
        ServerRuntime runtime,
        FirewallAccessRecord? record,
        FirewallPolicySnapshot policy,
        FirewallTargetResolution target)
    {
        if (record is null || record.RuleId == Guid.Empty || (!record.Configured && !record.RemovalPending))
            return Refuse(serverId, runtime, record, policy, target,
                "ChunkPilot has no firewall rule of its own to remove for this server.");

        if (!policy.RulesAvailable)
            return Refuse(serverId, runtime, record, policy, target,
                "Windows Firewall rules could not be fully verified, so nothing was removed.");

        var existing = WindowsFirewallPolicy.FindByName(record.RuleName, policy.Rules);
        if (existing is not null &&
            !WindowsFirewallPolicy.ProvesOwnership(record, existing))
            return Refuse(serverId, runtime, record, policy, target,
                "The rule with this identifier cannot be proven to be ChunkPilot's, so it will not be removed.");

        var command = new FirewallHelperCommand
        {
            Operation = FirewallHelperOperation.Remove,
            OperationId = Guid.NewGuid(),
            ServerId = serverId,
            RuleId = record.RuleId
        };
        var arguments = FirewallHelperCommandParser.ToArguments(command);
        var rehearsal = FirewallHelperCommandParser.Parse(arguments);
        if (rehearsal.Command is null)
            return Refuse(serverId, runtime, record, policy, target,
                $"ChunkPilot refused its own firewall request: {rehearsal.Error}");

        runtime.Pending = new PendingElevation(command, record.ToPlan(), target, false, clock.GetUtcNow());
        runtime.LivePhase = FirewallAccessPhase.WaitingForElevation;
        return new FirewallElevationTicket
        {
            Ready = true,
            OperationId = command.OperationId,
            Operation = FirewallHelperOperation.Remove,
            Arguments = arguments,
            Detail = $"Withdrawing {record.RuleName}.",
            State = Project(serverId, record, policy, target, runtime)
        };
    }

    private FirewallElevationTicket Refuse(
        Guid serverId,
        ServerRuntime runtime,
        FirewallAccessRecord? record,
        FirewallPolicySnapshot policy,
        FirewallTargetResolution target,
        string detail)
    {
        runtime.Pending = null;
        runtime.LivePhase = null;
        return new FirewallElevationTicket
        {
            Ready = false,
            Detail = detail,
            State = Project(serverId, record, policy, target, runtime)
        };
    }

    private static FirewallAccessFailure FailureFor(FirewallHelperExitCode code, FirewallAccessFailure fallback) =>
        code switch
        {
            FirewallHelperExitCode.OwnershipConflict => FirewallAccessFailure.OwnershipConflict,
            FirewallHelperExitCode.PolicyPrevented => FirewallAccessFailure.PolicyPrevented,
            FirewallHelperExitCode.AccessDenied => FirewallAccessFailure.AccessDenied,
            FirewallHelperExitCode.FirewallUnavailable => FirewallAccessFailure.ServiceUnavailable,
            FirewallHelperExitCode.InvalidArguments => FirewallAccessFailure.HelperFailed,
            FirewallHelperExitCode.UnexpectedFailure => FirewallAccessFailure.HelperFailed,
            _ => fallback
        };

    private static string Detail(FirewallHelperExitCode code, string launcherDetail, string observation) =>
        launcherDetail.Length > 0
            ? $"{observation} (helper result: {code}; {launcherDetail})"
            : $"{observation} (helper result: {code})";

    // ── Authoritative inputs ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Establishes the Java executable that is expected to own this server's listener.
    /// </summary>
    /// <remarks>
    /// A server that is running has already proven which executable it launched, and that recorded
    /// identity is preferred. A stopped server uses its own resolved configured runtime. Neither path
    /// consults <c>PATH</c>, the machine's Java installations, or anything the user typed free-form,
    /// and a launcher wrapper is refused by <see cref="WindowsFirewallPolicy.IsTrustworthyJavaRuntime"/>
    /// rather than substituted with something broader.
    /// </remarks>
    private async Task<FirewallTargetResolution> ResolveTargetAsync(
        Guid serverId, CancellationToken cancellationToken)
    {
        ManagedServer server;
        try
        {
            server = supervisor.Get(serverId);
        }
        catch (KeyNotFoundException)
        {
            return FirewallTargetResolution.Failed(FirewallTargetProblem.RuntimeUnavailable,
                "This server is no longer known to ChunkPilot.");
        }

        string? program = null;
        var source = "";
        var runtimeProblem = "";
        if (server.State is not ServerState.Stopped and not ServerState.Crashed)
        {
            var identity = await store.GetProcessIdentityAsync(serverId, cancellationToken).ConfigureAwait(false);
            if (identity is not null && identity.ExecutablePath.Length > 0)
            {
                program = identity.ExecutablePath;
                source = "the Java process this server is running";
            }
        }
        if (program is null)
        {
            var assignment = await store.GetJavaAssignmentAsync(serverId, cancellationToken).ConfigureAwait(false);
            if (assignment?.RuntimeId is not { } runtimeId)
                runtimeProblem =
                    "This stopped server has no ChunkPilot-managed Java runtime assignment. A broader rule was not substituted.";
            else
            {
                var managedRuntime = (await store.GetManagedJavaRuntimesAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(item => item.Id == runtimeId && item.IsManaged &&
                                            WindowsFirewallPolicy.SamePath(item.JavaPath, assignment.JavaPath));
                if (managedRuntime is null)
                    runtimeProblem =
                        "The Java assignment no longer matches a ChunkPilot-managed runtime. A broader rule was not substituted.";
                else
                {
                    program = managedRuntime.JavaPath;
                    source = assignment.Source.Length > 0
                        ? assignment.Source
                        : "this server's ChunkPilot-managed Java runtime";
                }
            }
        }
        return targets.Resolve(program, source, server.Definition.Port, runtimeProblem);
    }

    private async Task<FirewallPolicySnapshot> ReadPolicyAsync(
        bool forceRefresh, CancellationToken cancellationToken)
    {
        await policyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (!forceRefresh && cachedPolicy is not null && now - cachedPolicyAt < PolicyCacheLifetime)
                return cachedPolicy;
            var snapshot = firewall.Read();
            cachedPolicy = snapshot;
            cachedPolicyAt = now;
            return snapshot;
        }
        finally
        {
            policyGate.Release();
        }
    }

    private ServerRuntime Runtime(Guid serverId) => runtimes.GetOrAdd(serverId, _ => new ServerRuntime());

    public ValueTask DisposeAsync()
    {
        foreach (var runtime in runtimes.Values)
            runtime.Gate.Dispose();
        runtimes.Clear();
        policyGate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>One prepared privileged operation, held only in memory until it is answered.</summary>
    private sealed record PendingElevation(
        FirewallHelperCommand Command,
        FirewallRulePlan Plan,
        FirewallTargetResolution Target,
        bool PublicApproved,
        DateTimeOffset PreparedAt)
    {
        public bool HasExpired(DateTimeOffset now) => now - PreparedAt > ElevationLifetime;
    }

    /// <summary>In-memory only. A prompt that is open is not a fact worth surviving a restart.</summary>
    private sealed class ServerRuntime
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public PendingElevation? Pending { get; set; }
        public FirewallAccessPhase? LivePhase { get; set; }
        public FirewallAccessFailure TransientFailure { get; set; }
        public string TransientDetail { get; set; } = "";
    }
}

/// <summary>Turns a durable record back into the plan it describes, for verification and removal.</summary>
internal static class FirewallAccessRecordExtensions
{
    public static FirewallRulePlan ToPlan(this FirewallAccessRecord record) => new()
    {
        ServerId = record.ServerId,
        RuleId = record.RuleId,
        ProgramPath = record.ProgramPath,
        Port = record.Port,
        Transport = record.Transport,
        Profiles = record.Profiles
    };
}
