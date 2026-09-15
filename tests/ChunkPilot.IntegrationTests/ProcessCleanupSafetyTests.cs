using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.IntegrationTests;

public sealed class ProcessCleanupSafetyTests
{
    [Fact(Timeout = 30_000)]
    public async Task Managed_job_preserves_an_orphaned_grandchild_until_exact_force_cleanup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChunkPilot-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new AppDataPaths(Path.Combine(directory, "data"));
        var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var foreign = StartFixture("staged-validation-child", directory);
        Process? leaf = null;
        var server = new ManagedServer(new ServerDefinition
        {
            Name = "Exact orphan fixture", RootPath = directory, WorkingDirectory = directory,
            Executable = FixtureExecutable(), Arguments = $"owned-job-orphan-root {port}", Port = port,
            StartupTimeoutSeconds = 5, ShutdownTimeoutSeconds = 5, SaveTimeoutSeconds = 2
        }, new ProcessStatisticsProvider(), new MinecraftStatusClient(), store, paths, NullLogger<ManagedServer>.Instance);
        try
        {
            _ = await server.StartAsync();
            var pidPath = Path.Combine(directory, "owned-job-leaf.pid");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(pidPath) || server.RootProcessId is not null)
                await Task.Delay(50, deadline.Token);
            var identity = (await File.ReadAllTextAsync(pidPath, deadline.Token)).Split('|');
            leaf = Process.GetProcessById(int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(long.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture), leaf.StartTime.ToUniversalTime().Ticks);
            _ = leaf.SafeHandle;
            Assert.True(server.HasExactOwnedProcessAlive());
            Assert.False((await server.StopAsync(saveFirst: false)).Success);
            Assert.True(server.HasExactOwnedProcessAlive());
            Assert.False(leaf.HasExited);
            var restart = await server.StartAsync();
            Assert.False(restart.Success);
            Assert.True(server.HasExactOwnedProcessAlive());
            var force = await server.ForceTerminateAsync();
            Assert.True(force.Success, force.Message);
            await leaf.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(server.HasExactOwnedProcessAlive());
            Assert.False(foreign.HasExited);
            using var released = new TcpListener(IPAddress.Loopback, port);
            released.Start();
            released.Stop();
        }
        finally
        {
            _ = await server.ForceTerminateAsync();
            await server.DisposeAsync();
            if (leaf is not null)
            {
                if (!leaf.HasExited) leaf.Kill();
                await leaf.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                leaf.Dispose();
            }
            if (!foreign.HasExited) foreign.Kill();
            await foreign.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            foreign.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Atomic_server_start_preserves_approved_argument_quoting_environment_and_staged_restrictions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChunkPilot-job-args-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var arguments = new[] { "with space", "quoted\"value", "C:\\path with spaces\\", "" };
            var start = new ProcessStartInfo(FixtureExecutable())
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                Arguments = "owned-job-arguments " + string.Join(" ", arguments.Select(CommandLineQuoter.QuoteWindowsArgument))
            };
            start.Environment["CHUNKPILOT_FIXTURE_ARGUMENT_ENV"] = "fixture environment";
            using (var staged = WindowsStagedProcessJob.Create())
                Assert.Throws<ArgumentException>(() => staged.Start(start));
            using var owned = OwnedServerProcess.Start(start);
            var output = owned.StandardOutput.ReadToEndAsync();
            await owned.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await owned.WaitForEmptyAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            var lines = (await output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(arguments, System.Text.Json.JsonSerializer.Deserialize<string[]>(lines[0]));
            Assert.Equal("fixture environment", lines[1]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 5_000)]
    public async Task Previous_attempt_drain_honors_cancellation_without_completing_the_old_pumps()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ManagedServer.WaitForPreviousAttemptAsync(
            blocked.Task, Task.CompletedTask, null, cancellation.Token));
        Assert.False(blocked.Task.IsCompleted);
        blocked.SetResult();
    }

    [Fact(Timeout = 5_000)]
    public async Task Previous_attempt_drain_is_bounded_and_never_invents_completed_cleanup()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(() => ManagedServer.WaitForPreviousAttemptAsync(
            Task.CompletedTask, blocked.Task, null, CancellationToken.None, TimeSpan.FromMilliseconds(50)));
        Assert.False(blocked.Task.IsCompleted);
        blocked.SetResult();
        await ManagedServer.WaitForPreviousAttemptAsync(Task.CompletedTask, blocked.Task, null, CancellationToken.None);
    }

    [Fact(Timeout = 20_000)]
    public async Task Exited_root_cleanup_terminates_its_exact_child_and_preserves_a_foreign_process()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChunkPilot-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? child = null;
        using var foreign = StartFixture("staged-validation-child", directory);
        using var owned = StartFixture("staged-spawn-child-and-exit.jar", directory);
        try
        {
            // Pin the launched root's handle before it exits. A numeric PID alone is never authority.
            _ = owned.SafeHandle;
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var identity = (await File.ReadAllTextAsync(Path.Combine(directory, "staged-validation-child.pid"))).Split('|');
            child = Process.GetProcessById(int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(long.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture), child.StartTime.ToUniversalTime().Ticks);
            _ = child.SafeHandle;
            Assert.True(ProcessTree.HasLiveProcesses(owned));
            ProcessTree.Kill(owned);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(ProcessTree.HasLiveProcesses(owned));
            Assert.False(foreign.HasExited);
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                child.Dispose();
            }
            if (!owned.HasExited) owned.Kill(entireProcessTree: true);
            if (!foreign.HasExited) foreign.Kill();
            await foreign.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            owned.Dispose();
            foreign.Dispose();
            for (var retry = 0; ; retry++)
            {
                try { Directory.Delete(directory, recursive: true); break; }
                catch (IOException) when (retry < 30) { await Task.Delay(100); }
            }
        }
    }

    private static Process StartFixture(string argument, string directory)
    {
        var start = new ProcessStartInfo(FixtureExecutable())
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Fixture process did not start.");
    }

    private static string FixtureExecutable()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ChunkPilot.sln"))) root = root.Parent;
        return Path.Combine(root?.FullName ?? throw new DirectoryNotFoundException("Repository not found."),
            "tests", "ChunkPilot.FakeServer", "bin", "Release", "net10.0", "ChunkPilot.FakeServer.exe");
    }
}
