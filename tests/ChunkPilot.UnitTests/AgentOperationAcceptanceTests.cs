using ChunkPilot.App.WebUi;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class AgentOperationAcceptanceTests
{
    [Fact]
    public async Task Managed_content_response_loss_replays_the_full_exact_begin()
    {
        var request = Request();
        var accepted = Snapshot(request);
        var beginCalls = 0;

        var result = await AgentOperationAcceptance.BeginManagedContentInstallAsync(
            request,
            _ =>
            {
                beginCalls++;
                return beginCalls == 1
                    ? Task.FromException<ManagedContentOperationSnapshot>(
                        new IOException("The response frame was lost after acceptance."))
                    : Task.FromResult(accepted);
            },
            _ => Task.FromResult(new ManagedContentCancellationFenceResult(true, false, accepted)),
            CancellationToken.None);

        Assert.Equal(request.OperationId, result.OperationId);
        Assert.Equal(request.ServerId, result.ServerId);
        Assert.Equal(2, beginCalls);
    }

    [Fact]
    public async Task Lost_managed_mismatch_response_replays_and_surfaces_restart_or_authorization_conflict()
    {
        var request = Request();
        var beginCalls = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentOperationAcceptance.BeginManagedContentInstallAsync(
                request,
                _ =>
                {
                    beginCalls++;
                    return beginCalls == 1
                        ? Task.FromException<ManagedContentOperationSnapshot>(
                            new IOException("The mismatch response was lost."))
                        : Task.FromException<ManagedContentOperationSnapshot>(
                            new InvalidOperationException(
                                "That operation identity belongs to a request with different restart or plan authorization."));
                },
                _ => throw new InvalidOperationException("Cancellation fencing must not run."),
                CancellationToken.None));

        Assert.Contains("different restart or plan authorization", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, beginCalls);
    }

    [Fact]
    public async Task Lost_modpack_mismatch_response_replays_and_surfaces_full_plan_conflict()
    {
        var operationId = Guid.NewGuid();
        var plan = new ModpackCreationPlan { OperationId = operationId, ServerName = "New request" };
        var beginCalls = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentOperationAcceptance.BeginModpackCreationAsync(
                plan,
                _ =>
                {
                    beginCalls++;
                    return beginCalls == 1
                        ? Task.FromException<InstallOperationRequest>(
                            new IOException("The mismatch response was lost."))
                        : Task.FromException<InstallOperationRequest>(
                            new InvalidOperationException(
                                "That creation identity belongs to another exact modpack plan."));
                },
                _ => throw new InvalidOperationException("Cancellation fencing must not run."),
                CancellationToken.None));

        Assert.Contains("another exact modpack plan", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, beginCalls);
    }

    [Fact]
    public async Task Cancel_during_delayed_modpack_acceptance_waits_for_an_authoritative_prestart_fence()
    {
        var operationId = Guid.NewGuid();
        var plan = new ModpackCreationPlan { OperationId = operationId };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fenceCalls = 0;

        var result = await AgentOperationAcceptance.BeginModpackCreationAsync(
            plan,
            _ => Task.FromException<InstallOperationRequest>(
                new OperationCanceledException("The renderer stopped waiting for the acknowledgement.")),
            _ =>
            {
                fenceCalls++;
                return fenceCalls == 1
                    ? Task.FromException<ModpackCreationCancellationFenceResult>(
                        new IOException("The first fence acknowledgement was lost."))
                    : Task.FromResult(new ModpackCreationCancellationFenceResult(
                        true,
                        true,
                        CancelledCreation(operationId)));
            },
            cancellation.Token);

        Assert.Equal(operationId, result);
        Assert.Equal(2, fenceCalls);
    }

    [Fact]
    public async Task Cancel_during_delayed_managed_content_acceptance_reserves_terminal_identity()
    {
        var request = Request();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fenceCalls = 0;

        var result = await AgentOperationAcceptance.BeginManagedContentInstallAsync(
            request,
            _ => Task.FromException<ManagedContentOperationSnapshot>(
                new OperationCanceledException("The renderer stopped waiting for the acknowledgement.")),
            _ =>
            {
                fenceCalls++;
                return fenceCalls == 1
                    ? Task.FromException<ManagedContentCancellationFenceResult>(
                        new IOException("The first fence acknowledgement was lost."))
                    : Task.FromResult(new ManagedContentCancellationFenceResult(
                        true,
                        true,
                        Snapshot(request) with
                        {
                            IsTerminal = true,
                            Success = false,
                            IsCancellable = false,
                            Error = "The operation was cancelled.",
                            Progress = new ManagedContentProgress
                            {
                                Stage = ManagedContentOperationStage.Cancelled,
                                Message = "Cancelled before start",
                                Percent = 0
                            }
                        }));
            },
            cancellation.Token);

        Assert.Equal(request.OperationId, result.OperationId);
        Assert.True(result.IsTerminal);
        Assert.Equal(ManagedContentOperationStage.Cancelled, result.Progress.Stage);
        Assert.Equal(2, fenceCalls);
    }

    [Fact]
    public async Task Cancellation_capacity_response_is_not_terminal_and_is_retried()
    {
        var request = Request();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fenceCalls = 0;

        var result = await AgentOperationAcceptance.BeginManagedContentInstallAsync(
            request,
            _ => Task.FromException<ManagedContentOperationSnapshot>(
                new OperationCanceledException("The local caller cancelled.")),
            _ =>
            {
                fenceCalls++;
                return Task.FromResult(fenceCalls == 1
                    ? new ManagedContentCancellationFenceResult(false, false, null)
                    : new ManagedContentCancellationFenceResult(
                        true,
                        true,
                        Snapshot(request) with
                        {
                            IsTerminal = true,
                            Success = false,
                            IsCancellable = false,
                            Progress = new ManagedContentProgress
                            {
                                Stage = ManagedContentOperationStage.Cancelled,
                                Message = "Cancelled before start"
                            }
                        }));
            },
            cancellation.Token);

        Assert.Equal(request.OperationId, result.OperationId);
        Assert.Equal(2, fenceCalls);
    }

    [Fact]
    public async Task Empty_client_operation_ids_are_rejected_before_begin()
    {
        var managed = Request() with { OperationId = Guid.Empty };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AgentOperationAcceptance.BeginManagedContentInstallAsync(
                managed,
                _ => throw new InvalidOperationException("Begin must not run."),
                _ => throw new InvalidOperationException("Fence must not run."),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AgentOperationAcceptance.BeginModpackCreationAsync(
                new ModpackCreationPlan { OperationId = Guid.Empty },
                _ => throw new InvalidOperationException("Begin must not run."),
                _ => throw new InvalidOperationException("Fence must not run."),
                CancellationToken.None));
    }

    private static BeginManagedContentInstallRequest Request() => new(
        Guid.NewGuid(),
        "245755",
        "7682270",
        IncludeDependencies: true,
        RestartIfRunning: true,
        OperationId: Guid.NewGuid(),
        Provider: PluginProviderKind.CurseForge,
        PlanAuthorization: new ManagedContentPlanAuthorization
        {
            AuthorizationId = Guid.NewGuid(),
            Digest = new string('a', 64)
        });

    private static ManagedContentOperationSnapshot Snapshot(BeginManagedContentInstallRequest request) => new()
    {
        OperationId = request.OperationId,
        ServerId = request.ServerId,
        Kind = ManagedContentOperationKind.InstallAddonPlan,
        Provider = request.Provider.ToString(),
        ProjectId = request.ProjectId,
        VersionId = request.VersionId,
        Progress = new ManagedContentProgress
        {
            Stage = ManagedContentOperationStage.Queued,
            Message = "Accepted"
        }
    };

    private static InstallOperationSnapshot CancelledCreation(Guid operationId) => new()
    {
        OperationId = operationId,
        IsTerminal = true,
        Success = false,
        Outcome = CreationOutcome.NothingActivated,
        Error = "The operation was cancelled.",
        Progress = new InstallProgress
        {
            OperationId = operationId,
            State = InstallState.Cancelled,
            Phase = CreationPhase.Cancelling,
            Stage = CreationStage.Cancelled,
            CurrentStep = "Cancelled before start"
        }
    };
}
