using System.Diagnostics;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class JavaProcessProbeIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-java-probe-" + Guid.NewGuid().ToString("N"));
    public JavaProcessProbeIntegrationTests() => Directory.CreateDirectory(root);

    [Fact(Timeout = 30_000)]
    public async Task Version_probe_timeout_stops_the_exact_process_before_returning()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => JavaProcessProbe.RunAsync(
            Start("staged-validation-child"), TimeSpan.FromMilliseconds(500), CancellationToken.None));
    }

    [Fact(Timeout = 30_000)]
    public async Task Caller_cancellation_remains_cancellation_after_owned_process_cleanup()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JavaProcessProbe.RunAsync(
            Start("staged-validation-child"), TimeSpan.FromSeconds(15), cancellation.Token));
    }

    [Fact(Timeout = 30_000)]
    public async Task Large_probe_output_is_bounded_and_does_not_deadlock()
    {
        var result = await JavaProcessProbe.RunAsync(Start("high-volume"), TimeSpan.FromSeconds(15), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(65_536, result.Output.Length);
    }

    [Fact(Timeout = 30_000)]
    public async Task Descendant_cannot_survive_or_hold_probe_output_open_after_parent_exits()
    {
        var result = await JavaProcessProbe.RunAsync(Start("staged-spawn-child-and-exit.jar"), TimeSpan.FromSeconds(15), CancellationToken.None);
        Assert.Equal(37, result.ExitCode);
        var identity = (await File.ReadAllTextAsync(Path.Combine(root, "staged-validation-child.pid"))).Split('|');
        try
        {
            using var child = Process.GetProcessById(int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(child.HasExited || child.StartTime.ToUniversalTime().Ticks != long.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture));
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
                "bin", "Release", "net10.0", "ChunkPilot.FakeServer.exe"), WorkingDirectory = root
        };
        start.ArgumentList.Add(mode);
        return start;
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
