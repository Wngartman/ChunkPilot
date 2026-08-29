using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class ModpackCreationAcceptanceIntegrationTests
{
    [Fact]
    public async Task Concurrent_exact_replays_authorize_once_and_mismatched_replay_is_refused()
    {
        var coordinator = new InstallationCoordinator(null!, null!, null!);
        var plan = Plan();
        var authorizationCalls = 0;
        using var start = new ManualResetEventSlim();
        var starts = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return coordinator.BeginAuthorizedModpack(plan, candidate =>
            {
                Interlocked.Increment(ref authorizationCalls);
                return candidate;
            });
        })).ToArray();

        start.Set();
        var operationIds = await Task.WhenAll(starts);

        Assert.All(operationIds, operationId => Assert.Equal(plan.OperationId, operationId));
        Assert.Equal(1, authorizationCalls);
        Assert.True(coordinator.TryGet(plan.OperationId, out var accepted));
        Assert.Equal(plan.OperationId, accepted!.OperationId);

        var mismatch = Assert.Throws<InvalidOperationException>(() =>
            coordinator.BeginAuthorizedModpack(
                plan with { ServerName = "A different destination" },
                _ => throw new InvalidOperationException("Authorization must not run for a mismatch.")));
        Assert.Contains("another exact modpack request", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, authorizationCalls);
    }

    [Fact]
    public void Cancellation_winning_before_delayed_begin_reserves_the_exact_creation_and_revokes_preflight()
    {
        var coordinator = new InstallationCoordinator(null!, null!, null!);
        var registry = new CurseForgeCreationAuthorizationRegistry();
        var plan = Plan();
        registry.Register(Preflight(plan));
        var revokeCalls = 0;

        var fenced = coordinator.CancelOrFenceModpack(plan, authorizationId =>
        {
            Interlocked.Increment(ref revokeCalls);
            registry.Revoke(authorizationId);
        });
        var replayedFence = coordinator.CancelOrFenceModpack(plan, _ =>
            throw new InvalidOperationException("A replayed fence must not revoke twice."));

        Assert.True(fenced.FenceEstablished);
        Assert.True(fenced.BlockedBeforeStart);
        Assert.NotNull(fenced.Operation);
        Assert.Equal(plan.OperationId, fenced.Operation!.OperationId);
        Assert.True(fenced.Operation.IsTerminal);
        Assert.False(fenced.Operation.Success);
        Assert.Equal(CreationStage.Cancelled, fenced.Operation.Progress.Stage);
        Assert.True(replayedFence.FenceEstablished);
        Assert.True(replayedFence.BlockedBeforeStart);
        Assert.Equal(plan.OperationId, replayedFence.Operation!.OperationId);
        Assert.Equal(1, revokeCalls);
        Assert.Equal(0, registry.OutstandingCount);
        Assert.Throws<InvalidOperationException>(() => registry.Consume(plan));
        Assert.Throws<InvalidOperationException>(() => coordinator.CancelOrFenceModpack(
            plan with { ServerName = "A different exact request" },
            _ => throw new InvalidOperationException("A mismatch must not revoke anything.")));

        // This is the delayed original Begin arriving after its acknowledgement waiter cancelled.
        // It reattaches to the terminal reservation and cannot consume authorization or start work.
        var lateAcceptance = coordinator.BeginAuthorizedModpack(
            plan,
            _ => throw new InvalidOperationException(
                "The pre-start fence must prevent late authorization consumption."));

        Assert.Equal(plan.OperationId, lateAcceptance);
        Assert.True(coordinator.TryGet(lateAcceptance, out var lateSnapshot));
        Assert.True(lateSnapshot!.IsTerminal);
        Assert.Equal(CreationStage.Cancelled, lateSnapshot.Progress.Stage);
        Assert.Equal(0, registry.OutstandingCount);

        var replayWithAnotherOperation = plan with { OperationId = Guid.NewGuid() };
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginAuthorizedModpack(
            replayWithAnotherOperation,
            registry.DemandAndConsumeIfRequired));
    }

    [Fact]
    public void Begin_winning_before_cancellation_consumes_once_then_fences_the_registered_creation()
    {
        var coordinator = new InstallationCoordinator(null!, null!, null!);
        var registry = new CurseForgeCreationAuthorizationRegistry();
        var plan = Plan();
        registry.Register(Preflight(plan));
        var revokeCalls = 0;

        var accepted = coordinator.BeginAuthorizedModpack(
            plan,
            registry.DemandAndConsumeIfRequired);
        var fenced = coordinator.CancelOrFenceModpack(plan, authorizationId =>
        {
            Interlocked.Increment(ref revokeCalls);
            registry.Revoke(authorizationId);
        });
        var replay = coordinator.BeginAuthorizedModpack(
            plan,
            _ => throw new InvalidOperationException("An exact Begin replay must not authorize twice."));

        Assert.Equal(plan.OperationId, accepted);
        Assert.Equal(accepted, replay);
        Assert.True(fenced.FenceEstablished);
        Assert.False(fenced.BlockedBeforeStart);
        Assert.Equal(accepted, fenced.Operation!.OperationId);
        Assert.Equal(0, revokeCalls);
        Assert.Equal(0, registry.OutstandingCount);
        Assert.Throws<InvalidOperationException>(() => registry.Consume(plan));
    }

    [Fact]
    public void Cancellation_capacity_seals_unseen_modpack_begins_and_keeps_returning_terminal_evidence()
    {
        var coordinator = new InstallationCoordinator(
            null!,
            null!,
            null!,
            maximumPreCancelledModpackOperations: 2);
        var first = Plan();
        var lastRetained = Plan();

        Assert.True(coordinator.CancelOrFenceModpack(first).FenceEstablished);
        Assert.True(coordinator.CancelOrFenceModpack(lastRetained).FenceEstablished);

        var knownReplay = coordinator.BeginAuthorizedModpack(
            first,
            _ => throw new InvalidOperationException(
                "A retained exact cancellation replay must not authorize."));
        Assert.Equal(first.OperationId, knownReplay);
        Assert.True(coordinator.TryGet(knownReplay, out var knownSnapshot));
        Assert.True(knownSnapshot!.IsTerminal);
        Assert.Equal(CreationStage.Cancelled, knownSnapshot.Progress.Stage);
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginAuthorizedModpack(
            first with { ServerName = "Mismatched retained request" },
            candidate => candidate));

        var registry = new CurseForgeCreationAuthorizationRegistry();
        var afterSeal = Plan();
        registry.Register(Preflight(afterSeal));
        var revokeCalls = 0;

        ModpackCreationCancellationFenceResult FenceAfterSeal() =>
            coordinator.CancelOrFenceModpack(afterSeal, authorizationId =>
            {
                Interlocked.Increment(ref revokeCalls);
                registry.Revoke(authorizationId);
            });

        var fenced = FenceAfterSeal();
        var replayedFence = FenceAfterSeal();

        Assert.True(fenced.FenceEstablished);
        Assert.True(fenced.BlockedBeforeStart);
        Assert.True(fenced.Operation!.IsTerminal);
        Assert.False(fenced.Operation.Success);
        Assert.Equal(afterSeal.OperationId, fenced.Operation.OperationId);
        Assert.Equal(CreationStage.Cancelled, fenced.Operation.Progress.Stage);
        Assert.True(replayedFence.FenceEstablished);
        Assert.Equal(afterSeal.OperationId, replayedFence.Operation!.OperationId);
        Assert.Equal(2, revokeCalls);
        Assert.Equal(0, registry.OutstandingCount);

        var delayedBegin = Assert.Throws<InvalidOperationException>(() =>
            coordinator.BeginAuthorizedModpack(
                afterSeal,
                _ => throw new InvalidOperationException(
                    "The fail-closed coordinator must reject before authorization.")));
        Assert.Contains("fail-closed", delayedBegin.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restart ChunkPilot", delayedBegin.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Agent_rejects_an_empty_external_creation_operation_identity_before_authorization()
    {
        var coordinator = new InstallationCoordinator(null!, null!, null!);
        var authorizationCalls = 0;

        Assert.Throws<ArgumentException>(() => coordinator.BeginAuthorizedModpack(
            Plan() with { OperationId = Guid.Empty },
            candidate =>
            {
                Interlocked.Increment(ref authorizationCalls);
                return candidate;
            }));

        Assert.Equal(0, authorizationCalls);
    }

    private static ModpackCreationPlan Plan() => new()
    {
        OperationId = Guid.NewGuid(),
        PreflightOperationId = Guid.NewGuid(),
        SourceKind = ModpackCreationSource.CurseForgeOfficialServerPack,
        Source = "https://media.forgecdn.net/files/1/2/server.zip",
        Provider = UpdateProvider.CurseForge,
        ProjectId = "392141",
        ProjectName = "Fixture pack",
        VersionId = "6132545",
        ServerPackFileId = "6132552",
        VersionName = "5.0.7",
        MinecraftVersion = "1.20.1",
        Loader = "Forge",
        LoaderVersion = "47.3.0",
        RequiredJavaMajor = 17,
        ExpectedSha1 = new string('a', 40),
        ExpectedSizeBytes = 1,
        VerifiedClientArchiveSha256 = new string('b', 64),
        ServerName = "Acceptance fixture",
        Eula = new VanillaEulaAcceptance
        {
            Accepted = true,
            AcceptedAtUtc = DateTimeOffset.UnixEpoch,
            SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
        },
        ExperimentalRuntimeRiskAccepted = true
    };

    private static CurseForgeModpackPreflightResult Preflight(ModpackCreationPlan plan) => new()
    {
        OperationId = plan.PreflightOperationId!.Value,
        ProjectId = plan.ProjectId,
        ClientFileId = plan.VersionId,
        ServerPackFileId = plan.ServerPackFileId,
        State = CatalogReleasePreflightState.Ready,
        MinecraftVersion = plan.MinecraftVersion,
        Loader = plan.Loader,
        LoaderVersion = plan.LoaderVersion,
        RequiredJavaMajor = plan.RequiredJavaMajor,
        ClientDownloadUrl = "https://media.forgecdn.net/files/1/1/client.zip",
        ClientSha1 = new string('d', 40),
        ClientSha256 = plan.VerifiedClientArchiveSha256,
        ClientSizeBytes = 2,
        ServerPackDownloadUrl = plan.Source,
        ServerPackSha1 = plan.ExpectedSha1,
        ServerPackSizeBytes = plan.ExpectedSizeBytes
    };
}
