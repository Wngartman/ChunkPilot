using System.Diagnostics;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class OwnedAutomationProgramTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-owned-automation-" + Guid.NewGuid().ToString("N"));

    public OwnedAutomationProgramTests() => Directory.CreateDirectory(root);

    [Fact(Timeout = 30_000)]
    public async Task Output_larger_than_retention_is_drained_without_blocking_child_exit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await OwnedAutomationProgramRunner.RunAsync(Start("high-volume"), timeout.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(65_536, result.Output.Length);
    }

    [Fact(Timeout = 30_000)]
    public async Task Cancellation_terminates_the_exact_owned_process_job()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OwnedAutomationProgramRunner.RunAsync(Start("staged-validation-child"), timeout.Token));
        // RunAsync cannot propagate cancellation before its Job emptiness proof has completed.
    }

    [Fact(Timeout = 30_000)]
    public async Task Root_exit_does_not_leave_its_child_running_or_holding_output_open()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await OwnedAutomationProgramRunner.RunAsync(Start("staged-spawn-child-and-exit.jar"), timeout.Token);
        Assert.Equal(37, result.ExitCode);
        var identity = (await File.ReadAllTextAsync(Path.Combine(root, "staged-validation-child.pid"))).Split('|');
        var id = int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture);
        var creation = long.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var child = Process.GetProcessById(id);
            Assert.True(child.HasExited || child.StartTime.ToUniversalTime().Ticks != creation,
                "The exact owned descendant survived the completed automation.");
        }
        catch (ArgumentException) { }
    }

    private ProcessStartInfo Start(string mode)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "ChunkPilot.sln"))) repo = repo.Parent;
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(repo?.FullName ?? throw new DirectoryNotFoundException(), "tests", "ChunkPilot.FakeServer",
                "bin", "Release", "net10.0", "ChunkPilot.FakeServer.exe"),
            WorkingDirectory = root
        };
        start.ArgumentList.Add(mode);
        return start;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
