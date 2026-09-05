using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class CreationRetryIntegrationTests
{
    [Fact]
    public void Generic_install_cannot_bypass_the_native_retry_review()
    {
        var coordinator = new InstallationCoordinator(null!, null!, null!);
        Assert.Throws<InvalidOperationException>(() => coordinator.Begin(new ServerInstallRequest { RetryVerifiedInput = true }));
        Assert.Throws<InvalidOperationException>(() => coordinator.Begin(new ServerInstallRequest { RetryGeneration = 1 }));
    }

    [Fact]
    public async Task Restart_restores_failure_generation_and_inflight_retry_cannot_duplicate_or_discard()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-retry-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
        paths.EnsureCreated();
        try
        {
            await using var store = new ChunkPilotStore(paths);
            await store.InitializeAsync();
            var entry = new CreationJournalEntry
            {
                OperationId = Guid.NewGuid(), ServerId = Guid.NewGuid(), Outcome = CreationOutcome.StagingResumable,
                CanonicalDestination = Path.Combine(paths.ManagedServers, "fixture"), InstanceRoot = paths.ManagedServers,
                StartedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow,
                VerifiedInput = new CreationVerifiedInput { SizeBytes = 42, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }
            };
            await store.UpsertCreationJournalAsync(entry);
            var coordinator = new InstallationCoordinator(null!, null!, store, paths: paths);
            var restored = await coordinator.GetWithRecoveryAsync(entry.OperationId, CancellationToken.None);
            Assert.True(restored.CanRetry);
            Assert.True(restored.CanDiscard);
            var provider = new HoldingProvider();
            Assert.Equal(entry.OperationId, await coordinator.RetryModpackCreationAsync(new(entry.OperationId, 0), provider, CancellationToken.None));
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, provider.Calls);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RetryModpackCreationAsync(new(entry.OperationId, 0), provider, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.DiscardModpackCreationAsync(new(entry.OperationId, 1), CancellationToken.None));
            provider.Finish.SetException(new InvalidDataException("Fixture metadata is unavailable."));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!coordinator.Get(entry.OperationId).IsTerminal) await Task.Delay(10, timeout.Token);
            var restarted = new InstallationCoordinator(null!, null!, store, paths: paths);
            Assert.Equal(1, (await restarted.GetWithRecoveryAsync(entry.OperationId, CancellationToken.None)).RetryGeneration);
            await store.UpsertCreationJournalAsync(entry with { Outcome = CreationOutcome.RecoveryRequired });
            var unknown = await restarted.GetWithRecoveryAsync(entry.OperationId, CancellationToken.None);
            Assert.False(unknown.CanRetry);
            Assert.False(unknown.CanDiscard);
            Assert.Equal(CreationStage.RecoveryRequired, unknown.Progress.Stage);
            Assert.DoesNotContain("was not changed", unknown.Progress.CurrentStep, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class HoldingProvider : ICurseForgeModpackPreflightService
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ServerInstallRequest> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CurseForgeModpackPreflightResult> InspectAsync(CurseForgeModpackPreflightRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServerInstallRequest> RevalidateRetainedOfficialAsync(CreationJournalEntry entry,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.SetResult();
            return Finish.Task;
        }
    }
}
