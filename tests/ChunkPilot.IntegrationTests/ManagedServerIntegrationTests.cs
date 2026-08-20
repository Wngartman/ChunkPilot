using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.IntegrationTests;

public sealed class ManagedServerIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-integration-" + Guid.NewGuid().ToString("N"));
    private AppDataPaths paths = null!;
    private ChunkPilotStore store = null!;
    private ILoggerFactory loggerFactory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        paths = new AppDataPaths(Path.Combine(root, "appdata"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    }

    [Fact(Timeout = 30_000)]
    public async Task Start_captures_console_save_confirms_and_stop_exits_cleanly()
    {
        await using var server = CreateManaged("normal");
        var start = await server.StartAsync();
        Assert.True(start.Success, start.Message);
        var persistedIdentity = await store.GetProcessIdentityAsync(server.Definition.Id);
        Assert.NotNull(persistedIdentity);
        Assert.NotEqual(ProcessCreationIdentity.Unknown, persistedIdentity.ProcessCreationTicks);
        using (var live = System.Diagnostics.Process.GetProcessById(persistedIdentity.ProcessId))
        {
            Assert.True(ProcessIdentityPolicy.MatchesProcessInstance(
                persistedIdentity, live.Id, ProcessCreationIdentity.Of(live.SafeHandle),
                live.MainModule!.FileName, out _));
        }
        var save = await server.SaveAsync();
        Assert.True(save.Success, save.Message);
        var stop = await server.StopAsync(saveFirst: true);
        Assert.True(stop.Success, stop.Message);
        var snapshot = server.Snapshot(500);
        Assert.Equal(ServerState.Stopped, snapshot.State);
        Assert.Contains(snapshot.Console, line => line.Text.Contains("Done (0.123s)", StringComparison.Ordinal));
        Assert.Contains(snapshot.Console, line => line.Text.Contains("Saved the game", StringComparison.Ordinal));
        Assert.Contains(snapshot.Console, line => line.Text.Contains("Stopping server", StringComparison.Ordinal));
    }

    [Fact(Timeout = 30_000)]
    public async Task Stop_releases_configured_port_before_reporting_success()
    {
        var port = GetFreePort();
        var definition = Definition("normal") with
        {
            Port = port,
            Environment = new Dictionary<string, string>
            {
                ["CHUNKPILOT_FAKE_STATUS_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        await using var server = new ManagedServer(definition, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), store, paths, loggerFactory.CreateLogger<ManagedServer>(),
            consoleCapacity: 2_000);

        var start = await server.StartAsync();
        Assert.True(start.Success, start.Message);
        Assert.True(IsPortListening(port), $"Fixture never listened on {port}.");

        var stop = await server.StopAsync(saveFirst: true);

        Assert.True(stop.Success, stop.Message);
        Assert.Contains($"released port {port}", stop.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(IsPortListening(port), $"Port {port} was still listening after StopAsync succeeded.");
    }

    [Fact(Timeout = 30_000)]
    public async Task Stop_closes_a_batch_wrapper_left_at_pause_after_java_releases_the_port()
    {
        var port = GetFreePort();
        var serverRoot = Path.Combine(root, "servers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(serverRoot);
        var script = Path.Combine(serverRoot, "run.bat");
        await File.WriteAllTextAsync(script,
            $"@echo off\r\n{CommandLineQuoter.QuoteWindowsArgument(DotnetPath())} " +
            $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} normal\r\npause\r\n");
        var definition = Definition("normal") with
        {
            RootPath = serverRoot,
            WorkingDirectory = serverRoot,
            Executable = Environment.GetEnvironmentVariable("COMSPEC") ?? @"C:\Windows\System32\cmd.exe",
            Arguments = CommandLineQuoter.BuildCmdArguments(script),
            Port = port,
            Environment = new Dictionary<string, string>
            {
                ["CHUNKPILOT_FAKE_STATUS_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        await using var server = new ManagedServer(definition, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), store, paths, loggerFactory.CreateLogger<ManagedServer>(),
            consoleCapacity: 2_000);

        Assert.True((await server.StartAsync()).Success);
        var stop = await server.StopAsync(saveFirst: true);

        Assert.True(stop.Success, stop.Message);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.False(IsPortListening(port));
        Assert.Contains(server.Snapshot(200).Console,
            line => line.Text.Contains("leftover launcher script", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Timeout = 35_000)]
    public async Task Restart_orders_save_stop_then_new_start()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);
        var restart = await server.RestartAsync();
        Assert.True(restart.Success, restart.Message);
        var lines = server.Snapshot(1_000).Console.Select(line => line.Text).ToArray();
        var saveIndex = Array.FindIndex(lines, line => line.Contains("Saved the game", StringComparison.Ordinal));
        var stopIndex = Array.FindIndex(lines, line => line.Contains("Stopping server", StringComparison.Ordinal));
        var secondStart = Array.FindLastIndex(lines, line => line.Contains("Starting fake Minecraft server", StringComparison.Ordinal));
        Assert.True(saveIndex >= 0 && stopIndex > saveIndex && secondStart > stopIndex,
            $"Observed indexes save={saveIndex}, stop={stopIndex}, secondStart={secondStart}");
        var finalStop = await server.StopAsync();
        Assert.True(finalStop.Success, finalStop.Message);
    }

    [Fact(Timeout = 30_000)]
    public async Task Terraria_uses_the_shared_owned_lifecycle_without_minecraft_status_or_save_commands()
    {
        var definition = Definition("terraria") with
        {
            GameKind = ServerGameKind.Terraria,
            GameVersion = "1.4.5.6",
            MinecraftVersion = "",
            SaveCommand = "save",
            SaveFallbackCommand = "save",
            SaveConfirmationPattern = "World saved",
            StopCommand = "exit",
            ReadinessPattern = "Listening on port"
        };
        await using var server = new ManagedServer(definition, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), store, paths, loggerFactory.CreateLogger<ManagedServer>(),
            consoleCapacity: 2_000);

        var start = await server.StartAsync();
        Assert.True(start.Success, start.Message);
        Assert.True((await server.SendCommandAsync("playing")).Success);
        Assert.True((await server.SaveAsync()).Success);
        await Task.Delay(2_200);
        var running = server.Snapshot(200);
        Assert.Equal(ServerState.Running, running.State);
        Assert.Null(running.OnlinePlayers);
        Assert.Equal(PlayerStatusSource.Unsupported, running.PlayerStatus?.Source);
        Assert.DoesNotContain(running.Console, line => line.Text.Contains("save-all", StringComparison.OrdinalIgnoreCase));

        var stop = await server.StopAsync(saveFirst: true);
        Assert.True(stop.Success, stop.Message);
        var stopped = server.Snapshot(200);
        Assert.Equal(ServerState.Stopped, stopped.State);
        Assert.Contains(stopped.Console, line => line.Text.Contains("World saved", StringComparison.Ordinal));
        Assert.Contains(stopped.Console, line => line.Text.Contains("Server stopped", StringComparison.Ordinal));
    }

    [Fact(Timeout = 35_000)]
    public async Task Restartable_data_operation_keeps_restart_ownership_inside_the_agent()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);
        var marker = Path.Combine(server.Definition.RootPath, "plugin-change.marker");

        var result = await server.RunExclusiveRestartableDataOperationAsync(
            "applying a fixture plugin",
            restartIfRunning: true,
            _ =>
            {
                File.WriteAllText(marker, "applied");
                return Task.FromResult(marker);
            },
            (_, _) => throw new Xunit.Sdk.XunitException("Rollback must not run after a healthy restart."));

        Assert.Equal(marker, result);
        Assert.Equal(ServerState.Running, server.State);
        Assert.True(File.Exists(marker));
        var finalStop = await server.StopAsync();
        Assert.True(finalStop.Success, finalStop.Message);
    }

    [Fact(Timeout = 40_000)]
    public async Task Restartable_data_operation_rolls_back_a_change_that_breaks_startup()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);
        var original = server.Definition;
        var rolledBack = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.RunExclusiveRestartableDataOperationAsync(
                "applying a fixture plugin",
                restartIfRunning: true,
                _ =>
                {
                    server.UpdateDefinition(server.Definition with
                    {
                        Executable = Path.Combine(server.Definition.RootPath, "missing-java.exe")
                    });
                    return Task.FromResult(original);
                },
                (previous, _) =>
                {
                    server.UpdateDefinition(previous);
                    rolledBack = true;
                    return Task.CompletedTask;
                }));

        Assert.Contains("restored", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(rolledBack);
        Assert.Equal(ServerState.Running, server.State);
        Assert.Equal(original.Executable, server.Definition.Executable);
        Assert.True((await server.StopAsync()).Success);
    }

    [Fact(Timeout = 35_000)]
    public async Task Port_definition_change_waits_for_restart_when_process_is_running()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);
        var oldPort = server.Definition.Port;

        server.UpdateDefinition(server.Definition with { Port = oldPort + 1 });

        Assert.Equal(oldPort, server.Snapshot().Definition.Port);
        Assert.True((await server.StopAsync(saveFirst: false)).Success);
        Assert.True((await server.StartAsync()).Success);
        Assert.Equal(oldPort + 1, server.Snapshot().Definition.Port);
        Assert.True((await server.StopAsync(saveFirst: false)).Success);
    }

    [Fact(Timeout = 30_000)]
    public async Task Save_falls_back_for_old_server_command()
    {
        await using var server = CreateManaged("old-save", saveTimeoutSeconds: 2);
        Assert.True((await server.StartAsync()).Success);
        var save = await server.SaveAsync();
        Assert.True(save.Success, save.Message);
        var lines = server.Snapshot(500).Console.Select(line => line.Text).ToArray();
        Assert.Contains(lines, line => line.Contains("> save-all flush", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("> save-all", StringComparison.Ordinal));
        Assert.True((await server.StopAsync()).Success);
    }

    [Fact(Timeout = 25_000)]
    public async Task Shutdown_timeout_never_forces_without_explicit_call()
    {
        await using var server = CreateManaged("ignore-stop", shutdownTimeoutSeconds: 2);
        Assert.True((await server.StartAsync()).Success);
        var stop = await server.StopAsync(saveFirst: true);
        Assert.False(stop.Success);
        Assert.True(stop.RequiresForceConfirmation);
        Assert.Equal(ServerState.Unresponsive, server.State);
        var force = await server.ForceTerminateAsync();
        Assert.True(force.Success, force.Message);
        Assert.Equal(ServerState.Stopped, server.State);
    }

    [Fact(Timeout = 25_000)]
    public async Task Real_process_statistics_are_sampled()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);
        await Task.Delay(2_500);
        var snapshot = server.Snapshot();
        Assert.NotNull(snapshot.CurrentStatistics);
        Assert.True(snapshot.CurrentStatistics.ProcessCount >= 1);
        Assert.True(snapshot.CurrentStatistics.WorkingSetBytes > 0);
        Assert.True((await server.StopAsync()).Success);
    }

    [Theory(Timeout = 25_000)]
    [InlineData("bind-failure", "already in use")]
    [InlineData("known-startup-failure", "could not be opened")]
    [InlineData("immediate-crash", "code 7")]
    [InlineData("conflicting-startup-failures", "already in use")]
    public async Task Startup_failure_preserves_the_most_specific_evidence(string mode, string expected)
    {
        await using var server = CreateManaged(mode);
        var result = await server.StartAsync();
        Assert.False(result.Success);
        Assert.Contains(expected, result.Message, StringComparison.OrdinalIgnoreCase);
        var snapshot = server.Snapshot();
        Assert.Contains(expected, snapshot.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.False(snapshot.LastStartReachedReadiness);
    }

    [Fact(Timeout = 40_000)]
    public async Task New_process_attempt_starts_a_fresh_metric_series()
    {
        await using var server = CreateManaged("normal");
        var firstStart = await server.StartAsync();
        await Task.Delay(2_500);
        var firstCount = server.Snapshot().RecentStatistics.Count;
        var firstStop = await server.StopAsync(saveFirst: false);

        var secondAttemptBegan = DateTimeOffset.UtcNow;
        var secondStart = await server.StartAsync();
        await Task.Delay(2_500);
        var second = server.Snapshot();
        var timestampsAreCurrent = second.RecentStatistics.All(sample => sample.Timestamp >= secondAttemptBegan);
        var secondStop = await server.StopAsync(saveFirst: false);
        if (!secondStop.Success)
            await server.ForceTerminateAsync();
        Assert.True(firstStart.Success, firstStart.Message);
        Assert.True(firstCount >= 1, $"First attempt produced {firstCount} samples.");
        Assert.True(firstStop.Success, firstStop.Message);
        Assert.True(secondStart.Success, secondStart.Message);
        Assert.True(second.RecentStatistics.Count >= 1, $"Second attempt produced {second.RecentStatistics.Count} samples.");
        Assert.True(secondStop.Success, secondStop.Message);
        Assert.True(timestampsAreCurrent,
            $"Second attempt began {secondAttemptBegan:O}; oldest sample was {second.RecentStatistics.Min(sample => sample.Timestamp):O}.");
    }

    [Fact(Timeout = 40_000)]
    public async Task Multiple_servers_run_and_stop_concurrently_through_supervisor()
    {
        var backups = new BackupService(paths, store);
        await using var supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), backups, loggerFactory);
        await supervisor.InitializeAsync();
        // This case intentionally starts and stops two child processes at once. Keep the
        // fixture deadline strict, but leave enough scheduler headroom for a loaded test host.
        var first = Definition("normal") with
        {
            Id = Guid.NewGuid(),
            Name = "First",
            ShutdownTimeoutSeconds = 10
        };
        var second = Definition("stderr") with
        {
            Id = Guid.NewGuid(),
            Name = "Second",
            ShutdownTimeoutSeconds = 10
        };
        await supervisor.ImportAsync(first);
        await supervisor.ImportAsync(second);
        var started = await supervisor.StartAllAsync();
        Assert.All(started.Values, result => Assert.True(result.Success, result.Message));
        Assert.Equal(2, (await supervisor.DashboardAsync()).Servers.Count(server => server.State == ServerState.Running));
        var stopped = await supervisor.StopAllAsync();
        foreach (var (serverId, result) in stopped)
        {
            var snapshot = supervisor.Get(serverId).Snapshot(50);
            Assert.True(result.Success,
                $"{snapshot.Definition.Name} ({serverId:D}) failed to stop in state {snapshot.State} " +
                $"with root process {snapshot.RootProcessId?.ToString() ?? "none"}: {result.Message}{Environment.NewLine}" +
                string.Join(Environment.NewLine, snapshot.Console.Select(line => $"[{line.Stream}] {line.Text}")));
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Application_exit_cancels_cooperative_gate_owner_and_takes_lifecycle_path()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = server.RunExclusiveDataOperationAsync("synthetic cooperative operation",
            requireStopped: false, saveIfRunning: false, freezeWorldSaving: false,
            async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            });
        await entered.Task;

        var exit = await server.StopForApplicationExitAsync(
            "Application exit", TimeSpan.FromSeconds(2), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(exit.Success, exit.Message);
        Assert.Equal(ServerState.Stopped, server.State);
    }

    [Fact(Timeout = 30_000)]
    public async Task Application_exit_gate_deadline_is_bounded_and_retry_reaches_terminal_state()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = server.RunExclusiveDataOperationAsync("synthetic delayed rollback",
            requireStopped: false, saveIfRunning: false, freezeWorldSaving: false,
            async _ =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            });
        await entered.Task;

        var blocked = await server.StopForApplicationExitAsync(
            "Application exit", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.False(blocked.Success);
        Assert.Contains("remains alive", blocked.Message, StringComparison.OrdinalIgnoreCase);

        release.SetResult();
        await operation;
        var retried = await server.StopForApplicationExitAsync(
            "Application exit", TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(retried.Success, retried.Message);
        Assert.False(server.HasExactOwnedProcessAlive());
    }

    [Fact(Timeout = 30_000)]
    public async Task Application_exit_continues_after_cancelled_operation_failure()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = server.RunExclusiveDataOperationAsync("synthetic cancellation rollback failure",
            requireStopped: false, saveIfRunning: false, freezeWorldSaving: false,
            async token =>
            {
                entered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException("synthetic rollback failure");
                }
                return true;
            });
        await entered.Task;

        var exit = await server.StopForApplicationExitAsync(
            "Unexpected UI exit", TimeSpan.FromSeconds(2), CancellationToken.None);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Contains("rollback failure", failure.Message);
        Assert.True(exit.Success, exit.Message);
        Assert.False(server.HasExactOwnedProcessAlive());
    }

    [Fact(Timeout = 30_000)]
    public async Task Legacy_detached_identity_fails_closed_without_killing_same_executable_process()
    {
        using var candidate = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = DotnetPath(),
                Arguments = $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} survive-eof",
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        Assert.True(candidate.Start());
        await using var server = CreateManaged("normal");
        server.MarkDetached(new ProcessIdentity
        {
            ServerId = server.Definition.Id,
            ProcessId = candidate.Id,
            ProcessStartTime = new DateTimeOffset(candidate.StartTime),
            ProcessCreationTicks = ProcessCreationIdentity.Unknown,
            ExecutablePath = candidate.MainModule!.FileName,
            WorkingDirectory = root,
            CommandSignature = "legacy"
        });
        try
        {
            var result = await server.ForceTerminateAsync("Application exit");

            Assert.False(result.Success);
            Assert.Contains("legacy", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(candidate.HasExited);
        }
        finally
        {
            if (!candidate.HasExited)
                candidate.Kill(entireProcessTree: true);
            await candidate.WaitForExitAsync();
        }
    }

    [Fact(Timeout = 35_000)]
    public async Task Application_exit_stop_requests_save_then_graceful_stop_then_exact_bounded_escalation()
    {
        var backups = new BackupService(paths, store);
        await using var supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), backups, loggerFactory);
        await supervisor.InitializeAsync();
        var port = GetFreePort();
        var definition = Definition("ignore-stop") with
        {
            Id = Guid.NewGuid(),
            ShutdownTimeoutSeconds = 2,
            Port = port,
            Environment = new Dictionary<string, string>
            {
                ["CHUNKPILOT_FAKE_STATUS_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        await supervisor.ImportAsync(definition);
        Assert.True((await supervisor.Get(definition.Id).StartAsync()).Success);
        Assert.True(IsPortListening(port));

        var results = await supervisor.StopAllAsync(
            "Unexpected UI exit", escalateOnFailure: true, cancellationToken: CancellationToken.None);

        Assert.True(results[definition.Id].Success, results[definition.Id].Message);
        Assert.Equal(ServerState.Stopped, supervisor.Get(definition.Id).State);
        Assert.False(IsPortListening(port));
        var lines = supervisor.Get(definition.Id).Snapshot(500).Console.Select(line => line.Text).ToArray();
        var save = Array.FindIndex(lines, line => line.Contains("Saved the game", StringComparison.Ordinal));
        var stop = Array.FindIndex(lines, line => line.Contains("> stop", StringComparison.Ordinal));
        Assert.True(save >= 0 && stop > save, $"Observed save={save}, stop={stop}.");
    }

    [Fact(Timeout = 30_000)]
    public async Task Manual_stop_all_remains_distinct_and_does_not_force_an_unresponsive_server()
    {
        var backups = new BackupService(paths, store);
        await using var supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), backups, loggerFactory);
        await supervisor.InitializeAsync();
        var definition = Definition("ignore-stop") with
        {
            Id = Guid.NewGuid(),
            ShutdownTimeoutSeconds = 2
        };
        await supervisor.ImportAsync(definition);
        Assert.True((await supervisor.Get(definition.Id).StartAsync()).Success);

        var results = await supervisor.StopAllAsync(
            "Manual", escalateOnFailure: false, cancellationToken: CancellationToken.None);

        Assert.False(results[definition.Id].Success);
        Assert.True(results[definition.Id].RequiresForceConfirmation);
        Assert.Equal(ServerState.Unresponsive, supervisor.Get(definition.Id).State);
        Assert.True((await supervisor.Get(definition.Id).ForceTerminateAsync()).Success);
    }

    [Fact(Timeout = 30_000)]
    public async Task Supported_server_launch_adds_nogui_and_keeps_console_capture()
    {
        var definition = Definition("normal") with { Ecosystem = ServerEcosystem.Vanilla, RunInBackground = true };
        await using var server = new ManagedServer(definition, new ProcessStatisticsProvider(), new MinecraftStatusClient(),
            store, paths, loggerFactory.CreateLogger<ManagedServer>(), consoleCapacity: 2_000);
        Assert.True((await server.StartAsync()).Success);
        var snapshot = server.Snapshot(100);
        Assert.True(snapshot.ConsoleConnected);
        Assert.Contains(snapshot.Console, line =>
            line.Stream == "ChunkPilot" && line.Text.EndsWith(" normal nogui", StringComparison.OrdinalIgnoreCase));
        Assert.True((await server.StopAsync()).Success);
    }

    [Fact(Timeout = 30_000)]
    public async Task High_volume_console_is_bounded_and_remains_responsive()
    {
        await using var server = CreateManaged("high-volume");
        Assert.True((await server.StartAsync()).Success);
        var snapshot = server.Snapshot(2_000);
        Assert.InRange(snapshot.Console.Count, 1, 2_000);
        Assert.Contains(snapshot.Console, line => line.Text.Contains("Done (0.123s)", StringComparison.Ordinal));
        Assert.True((await server.StopAsync()).Success);
    }

    [Fact(Timeout = 20_000)]
    public async Task Manual_stop_cancels_a_pending_crash_restart()
    {
        var definition = Definition("crash") with
        {
            CrashRestartEnabled = true,
            // Keep the crashed state observable after StartAsync returns. A one-second delay races
            // the readiness/status work and can restart before this test begins its state wait.
            CrashRestartDelaySeconds = 10,
            CrashRestartLimit = 3
        };
        await using var server = new ManagedServer(definition, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), store, paths, loggerFactory.CreateLogger<ManagedServer>());
        Assert.True((await server.StartAsync()).Success);
        await WaitForStateAsync(server, ServerState.Crashed, timeoutSeconds: 12);
        var analysisDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (server.Snapshot().LastCrashAnalysis is null && DateTimeOffset.UtcNow < analysisDeadline)
            await Task.Delay(50);
        var crashed = server.Snapshot();
        Assert.True(crashed.LastStartReachedReadiness);
        Assert.NotNull(crashed.LastCrashAnalysis);
        Assert.Equal(9, crashed.LastCrashAnalysis.ExitCode);
        Assert.Equal(CrashConfidence.Unknown, crashed.LastCrashAnalysis.Confidence);
        var persisted = await store.GetLatestCrashAnalysisAsync(server.Definition.Id);
        Assert.Equal(crashed.LastCrashAnalysis.ReportId, persisted?.ReportId);
        Assert.True((await server.StopAsync(saveFirst: false)).Success);
        await Task.Delay(2_500);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.Single(server.Snapshot(1_000).Console, line =>
            line.Text.Contains("Starting fake Minecraft server", StringComparison.Ordinal));
    }

    [Fact(Timeout = 30_000)]
    public async Task World_export_uses_save_off_flush_and_save_on_order()
    {
        await using var server = CreateManaged("normal");
        var worldPath = Path.Combine(server.Definition.RootPath, "world");
        Directory.CreateDirectory(worldPath);
        await File.WriteAllTextAsync(Path.Combine(worldPath, "level.dat"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(server.Definition.RootPath, "server.properties"), "level-name=world\r\n");
        Assert.True((await server.StartAsync()).Success);
        var manager = new WorldManager(paths, new SafeFileService(paths));
        var world = Assert.Single(manager.List(server.Definition));
        var destination = Path.Combine(root, "exports");
        _ = await server.RunExclusiveDataOperationAsync("exporting a world", requireStopped: false,
            saveIfRunning: true, freezeWorldSaving: true,
            token => manager.ExportAsync(server.Definition, world, destination, token));
        await Task.Delay(100);
        var lines = server.Snapshot(1_000).Console.Select(line => line.Text).ToArray();
        var saveOff = Array.FindIndex(lines, line => line.Contains("> save-off", StringComparison.Ordinal));
        var flush = Array.FindIndex(lines, line => line.Contains("> save-all flush", StringComparison.Ordinal));
        var saveOn = Array.FindIndex(lines, line => line.Contains("> save-on", StringComparison.Ordinal));
        Assert.True(saveOff >= 0 && flush > saveOff && saveOn > flush,
            $"Observed save-off={saveOff}, flush={flush}, save-on={saveOn}");
        Assert.True((await server.StopAsync()).Success);
    }

    [Fact(Timeout = 30_000)]
    public async Task Whitelist_changes_use_live_console_commands()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);
        var add = await server.SendCommandAsync(WhitelistService.AddCommand("FixturePlayer"));
        var remove = await server.SendCommandAsync(WhitelistService.RemoveCommand("FixturePlayer"));
        Assert.True(add.Success);
        Assert.True(remove.Success);
        await Task.Delay(100);
        var lines = server.Snapshot(500).Console.Select(line => line.Text).ToArray();
        Assert.Contains(lines, line => line.Contains("> whitelist add FixturePlayer", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("> whitelist remove FixturePlayer", StringComparison.Ordinal));
        Assert.True((await server.StopAsync()).Success);
    }

    [Fact(Timeout = 30_000)]
    public async Task Stopped_gamerules_are_applied_after_next_readiness()
    {
        await using var server = CreateManaged("normal");
        await store.SetSettingAsync(
            $"pending-gamerules:{server.Definition.Id:D}",
            """{"keepInventory":"true","playersSleepingPercentage":"50"}""");
        Assert.True((await server.StartAsync()).Success);
        await Task.Delay(100);
        var lines = server.Snapshot(500).Console.Select(line => line.Text).ToArray();
        Assert.Contains(lines, line => line.Contains("> gamerule keepInventory true", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("> gamerule playersSleepingPercentage 50", StringComparison.Ordinal));
        Assert.Equal("", await store.GetSettingAsync($"pending-gamerules:{server.Definition.Id:D}"));
        Assert.True((await server.StopAsync()).Success);
    }

    private ManagedServer CreateManaged(string mode, int saveTimeoutSeconds = 5, int shutdownTimeoutSeconds = 5)
    {
        var definition = Definition(mode) with
        {
            SaveTimeoutSeconds = saveTimeoutSeconds,
            ShutdownTimeoutSeconds = shutdownTimeoutSeconds
        };
        return new ManagedServer(definition, new ProcessStatisticsProvider(), new MinecraftStatusClient(),
            store, paths, loggerFactory.CreateLogger<ManagedServer>(), consoleCapacity: 2_000);
    }

    private ServerDefinition Definition(string mode)
    {
        var serverRoot = Path.Combine(root, "servers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(serverRoot);
        return new ServerDefinition
        {
            Name = $"Fake {mode}",
            RootPath = serverRoot,
            Executable = DotnetPath(),
            Arguments = $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} {mode}",
            WorkingDirectory = serverRoot,
            ReadinessPattern = @"Done \(.+?\)!|For help, type",
            StartupTimeoutSeconds = 10,
            ShutdownTimeoutSeconds = 5,
            SaveTimeoutSeconds = 5,
            Port = GetFreePort()
        };
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate ChunkPilot repository root.");
    }

    private static string DotnetPath() => Path.Combine(RepositoryRoot(), ".tools", "dotnet", "dotnet.exe");

    private static string FakeServerDll() =>
        Path.Combine(RepositoryRoot(), "tests", "ChunkPilot.FakeServer", "bin", "Release", "net10.0", "ChunkPilot.FakeServer.dll");

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsPortListening(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);

    private static async Task WaitForStateAsync(
        ManagedServer server,
        ServerState expected,
        int timeoutSeconds = 5)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (server.State == expected)
                return;
            await Task.Delay(50);
        }
        Assert.Equal(expected, server.State);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        loggerFactory.Dispose();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
