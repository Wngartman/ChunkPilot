using ChunkPilot.Agent;
using ChunkPilot.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.IntegrationTests;

public sealed class AutomationTriggerSafetyTests
{
    [Fact]
    public void Missing_volume_does_not_abort_other_automation_trigger_checks()
    {
        var worker = new AutomationWorker(null!, null!, null!, NullLogger<AutomationWorker>.Instance);
        var current = new ServerSnapshot { Definition = new() { RootPath = "" }, State = ServerState.Running };
        Assert.False(worker.ShouldRun(new() { Trigger = AutomationTriggerKind.LowDiskSpace, TriggerValue = "10" }, current, null));
        Assert.True(worker.ShouldRun(new() { Trigger = AutomationTriggerKind.ServerReady }, current, null));
    }

    [Fact]
    public void Impossible_memory_threshold_cannot_wrap_into_a_false_alarm()
    {
        var worker = new AutomationWorker(null!, null!, null!, NullLogger<AutomationWorker>.Instance);
        var current = new ServerSnapshot { Definition = new(), State = ServerState.Running };
        Assert.False(worker.ShouldRun(new() { Trigger = AutomationTriggerKind.HighRam,
            TriggerValue = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) }, current, null));
    }
}
