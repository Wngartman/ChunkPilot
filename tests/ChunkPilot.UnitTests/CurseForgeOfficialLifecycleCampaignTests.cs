using ChunkPilot.Certification;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeOfficialLifecycleCampaignTests
{
    [Fact]
    public async Task Restart_follows_proven_initial_cleanup_and_has_separate_evidence()
    {
        var report = new CurseForgeRuntimeCertificationReport();
        var calls = new List<string>();
        await CurseForgeRuntimeCertificationSession.ExerciseFreshOfficialLifecyclesAsync(report,
            _ =>
            {
                calls.Add("lifecycle");
                report.Lifecycle.Add(new("Start", "Running", true, "fixture", 1));
                report.Lifecycle.Add(new("Stop", "Stopped", true, "fixture save-first", 1));
                return Task.CompletedTask;
            },
            _ => { calls.Add("cleanup"); return Task.FromResult(Clean()); }, CancellationToken.None);

        Assert.Equal(["lifecycle", "cleanup", "lifecycle", "cleanup"], calls);
        Assert.Equal(["initial", "initial", "restart", "restart"], report.Lifecycle.Select(item => item.Cycle));
        Assert.Equal(["initial server lifecycle and cleanup", "restart server lifecycle and cleanup"],
            report.Steps.Select(item => item.Name));
        Assert.All(report.Steps, item => Assert.Equal("PASSED", item.Status));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Every_unproven_initial_cleanup_condition_prevents_restart(int missingCondition)
    {
        var report = new CurseForgeRuntimeCertificationReport();
        var calls = 0;
        var clean = Clean();
        var incomplete = missingCondition switch
        {
            0 => clean with { TaskServerInactive = false },
            1 => clean with { TaskServerProcessIdentityCaptured = false },
            2 => clean with { TaskServerRootProcessExited = false },
            3 => clean with { PortListenerAbsent = false },
            4 => clean with { NoPartialArtifacts = false },
            5 => clean with { NoUnsafeStagingResidue = false },
            _ => clean with { ListenerPidOwnershipVerified = false }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CurseForgeRuntimeCertificationSession.ExerciseFreshOfficialLifecyclesAsync(report,
                _ => { calls++; return Task.CompletedTask; },
                _ => Task.FromResult(incomplete), CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Equal("FAILED", Assert.Single(report.Steps).Status);
        Assert.False(report.Success);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task Lifecycle_or_cleanup_failure_preserves_which_cycle_failed(int failingCycle, bool duringCleanup)
    {
        var report = new CurseForgeRuntimeCertificationReport();
        var cycle = 0;
        await Assert.ThrowsAsync<IOException>(() =>
            CurseForgeRuntimeCertificationSession.ExerciseFreshOfficialLifecyclesAsync(report,
                _ =>
                {
                    cycle++;
                    report.Lifecycle.Add(new("Start", "Running", true, "fixture", 1));
                    if (cycle == failingCycle && !duringCleanup) throw new IOException("synthetic lifecycle failure");
                    return Task.CompletedTask;
                },
                _ =>
                {
                    if (cycle == failingCycle && duringCleanup) throw new IOException("synthetic cleanup failure");
                    return Task.FromResult(Clean());
                }, CancellationToken.None));
        Assert.Equal(failingCycle, cycle);
        Assert.Equal(failingCycle, report.Steps.Count);
        Assert.Equal("FAILED", report.Steps[^1].Status);
        Assert.Equal(failingCycle == 1 ? "initial" : "restart", report.Lifecycle[^1].Cycle);
        if (failingCycle == 2) Assert.Equal("PASSED", report.Steps[0].Status);
        Assert.False(report.Success);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_before_first_launch_or_between_cycles_never_starts_another_server(bool beforeFirst)
    {
        var report = new CurseForgeRuntimeCertificationReport();
        using var cancellation = new CancellationTokenSource();
        if (beforeFirst) cancellation.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CurseForgeRuntimeCertificationSession.ExerciseFreshOfficialLifecyclesAsync(report,
                _ => { calls++; return Task.CompletedTask; },
                _ => { cancellation.Cancel(); return Task.FromResult(Clean()); }, cancellation.Token));
        Assert.Equal(beforeFirst ? 0 : 1, calls);
        Assert.Equal(beforeFirst ? 0 : 1, report.Steps.Count);
    }

    [Fact]
    public void Restart_gate_does_not_require_the_live_agent_to_exit_and_keeps_keyless_resume_forbidden()
    {
        Assert.True(CurseForgeRuntimeCertificationSession.ServerStoppedPostconditionsPassed(Clean()));
        Assert.False(CurseForgeRuntimeCertificationSession.ServerStoppedPostconditionsPassed(null));
        Assert.False(CurseForgeRuntimeCertificationSession.TaskServerCleanupPassed(Clean()));
        Assert.Throws<ArgumentException>(() =>
            CurseForgeRuntimeCertificationCommand.ValidateAccessSelection(true, "", "prior.json"));
    }

    private static CertificationCleanupPostconditionsEvidence Clean() => new()
    {
        TaskServerInactive = true,
        TaskServerProcessIdentityCaptured = true,
        TaskServerRootProcessExited = true,
        PortListenerAbsent = true,
        NoPartialArtifacts = true,
        NoUnsafeStagingResidue = true,
        ListenerPidOwnershipVerified = true
    };
}
