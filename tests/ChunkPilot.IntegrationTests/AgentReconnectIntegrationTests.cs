using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

[Collection("Agent pipe")]
public sealed class AgentReconnectIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task Agent_accepts_multiple_sequential_ui_connections_and_shuts_down_cleanly()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-agent-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid().ToString("N");
        var pipeName = ChunkPilotConstants.PipeNameFor(instanceId);
        Directory.CreateDirectory(root);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = DotnetPath(),
                Arguments = CommandLineQuoter.QuoteWindowsArgument(AgentDll()),
                WorkingDirectory = RepositoryRoot(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.Environment["CHUNKPILOT_DATA_ROOT"] = root;
        process.StartInfo.Environment["CHUNKPILOT_INSTANCE_ID"] = instanceId;
        process.Start();
        try
        {
            OperationResult? first = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline && first is null)
            {
                try { first = await SendAsync<OperationResult>(pipeName, "Ping"); }
                catch (Exception exception) when (exception is IOException or TimeoutException) { await Task.Delay(200); }
            }
            Assert.NotNull(first);
            var second = await SendAsync<OperationResult>(pipeName, "Ping");
            Assert.True(second.Success);
            var dashboard = await SendAsync<DashboardSnapshot>(pipeName, "Dashboard");
            Assert.True(dashboard.AgentConnected);
            var shutdown = await SendAsync<OperationResult>(pipeName, "ShutdownAgent");
            Assert.True(shutdown.Success);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task Safe_application_exit_returns_immediately_then_agent_saves_stops_and_exits()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-safe-exit-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid().ToString("N");
        var pipeName = ChunkPilotConstants.PipeNameFor(instanceId);
        var definition = await CreateStoredFakeServerAsync(root, "slow-stop", TestPortAllocator.Reserve());
        using var process = StartAgent(root, instanceId);
        try
        {
            await WaitForAgentAsync(pipeName);
            var session = await RegisterCurrentUiAsync(pipeName);
            Assert.True((await SendAsync<OperationResult>(pipeName, "Start",
                AuthorizedServerRequest(definition.Id, session, PublicConnectivityOperation.StartServer))).Success);
            var stopwatch = Stopwatch.StartNew();
            var accepted = await SendAsync<OperationResult>(
                pipeName,
                "SafeApplicationExit",
                new SafeApplicationExitRequest(session.Session.SessionId, [definition.Id], DateTimeOffset.UtcNow)
                {
                    SessionCapability = session.SessionCapability
                });
            stopwatch.Stop();
            Assert.True(accepted.Success);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"SafeApplicationExit response took {stopwatch.Elapsed}.");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
            var consoleLog = Directory.EnumerateFiles(Path.Combine(root, "Logs", "Console"), "*.log").Single();
            var text = await File.ReadAllTextAsync(consoleLog);
            Assert.Contains("Saved the game", text);
            Assert.Contains("Stopping server", text);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 40_000)]
    public async Task Live_exact_UI_with_disconnected_pipes_keeps_hosting_until_safe_exit()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-ui-crash-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid().ToString("N");
        var pipeName = ChunkPilotConstants.PipeNameFor(instanceId);
        var definition = await CreateStoredFakeServerAsync(root, "normal", TestPortAllocator.Reserve());
        using var process = StartAgent(root, instanceId);
        try
        {
            await WaitForAgentAsync(pipeName);
            var session = await RegisterCurrentUiAsync(pipeName);
            Assert.True((await SendAsync<OperationResult>(
                pipeName, "Start",
                AuthorizedServerRequest(definition.Id, session, PublicConnectivityOperation.StartServer))).Success);
            await Task.Delay(5_500);
            Assert.False(process.HasExited);
            Assert.Contains((await SendAsync<DashboardSnapshot>(pipeName, "Dashboard")).Servers,
                server => server.Definition.Id == definition.Id && server.State == ServerState.Running);
            var exit = await SendAsync<OperationResult>(pipeName, "SafeApplicationExit",
                new SafeApplicationExitRequest(session.Session.SessionId, [definition.Id], DateTimeOffset.UtcNow)
                {
                    SessionCapability = session.SessionCapability
                });
            Assert.True(exit.Success);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Production_startup_cleans_stale_exposure_before_suppressing_restoration()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-stale-startup-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid().ToString("N");
        var pipeName = ChunkPilotConstants.PipeNameFor(instanceId);
        var port = TestPortAllocator.Reserve();
        var definition = await CreateStoredFakeServerAsync(root, "normal", port);
        await using (var store = new ChunkPilotStore(new AppDataPaths(root)))
        {
            await store.InitializeAsync();
            await store.SetRunningStateAsync(definition.Id, AutostartMode.AgentStart,
                wasRunning: true, LifecycleIntentKind.None);
            await store.UpsertRouterMappingAsync(new RouterMappingRecord
            {
                ServerId = definition.Id,
                DirectInternetEnabled = true,
                ConsentGranted = true,
                PublicLeaseId = Guid.NewGuid(),
                PublicLeaseGeneration = 7,
                PublicLifecycleEpoch = 1
            });
        }

        using var process = StartAgent(root, instanceId);
        try
        {
            await WaitForAgentAsync(pipeName);
            var dashboard = await SendAsync<DashboardSnapshot>(pipeName, "Dashboard");
            var server = Assert.Single(dashboard.Servers);

            Assert.Equal(ServerState.Stopped, server.State);
            Assert.False(IsPortListening(port));
            await using var store = new ChunkPilotStore(new AppDataPaths(root));
            await store.InitializeAsync();
            var mapping = await store.GetRouterMappingAsync(definition.Id);
            Assert.NotNull(mapping);
            Assert.False(mapping.DirectInternetEnabled);
            Assert.False(mapping.ConsentGranted);

            Assert.True((await SendAsync<OperationResult>(pipeName, "ShutdownAgent")).Success);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 45_000)]
    public async Task Taskkill_equivalent_exact_UI_process_death_safely_stops_server_and_exits_agent()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-ui-process-death-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid().ToString("N");
        var pipeName = ChunkPilotConstants.PipeNameFor(instanceId);
        var port = TestPortAllocator.Reserve();
        var definition = await CreateStoredFakeServerAsync(root, "normal", port);
        using var agent = StartAgent(root, instanceId);
        using var uiOwner = StartSyntheticUiOwner(pipeName);
        try
        {
            await WaitForAgentAsync(pipeName);
            var session = await ReadSyntheticUiSessionAsync(uiOwner);
            Assert.True((await SendAsync<OperationResult>(pipeName, "Start",
                AuthorizedServerRequest(definition.Id, session, PublicConnectivityOperation.StartServer))).Success);
            Assert.True(IsPortListening(port));

            uiOwner.Kill(entireProcessTree: true);
            await uiOwner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(0, agent.ExitCode);
            Assert.False(IsPortListening(port));
            var consoleLog = Directory.EnumerateFiles(Path.Combine(root, "Logs", "Console"), "*.log").Single();
            var text = await File.ReadAllTextAsync(consoleLog);
            Assert.Contains("Saved the game", text);
            Assert.Contains("Stopping server", text);
        }
        finally
        {
            if (!uiOwner.HasExited)
                uiOwner.Kill(entireProcessTree: true);
            if (!agent.HasExited)
                agent.Kill(entireProcessTree: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Named_pipe_caller_cannot_mint_a_capability_for_another_live_process()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-session-caller-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid().ToString("N");
        var pipeName = ChunkPilotConstants.PipeNameFor(instanceId);
        using var agent = StartAgent(root, instanceId);
        using var otherProcess = StartPassiveProcess();
        try
        {
            await WaitForAgentAsync(pipeName);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => RegisterUiAsync(pipeName, otherProcess));
            Assert.Contains("named-pipe caller", exception.Message, StringComparison.OrdinalIgnoreCase);

            var ownSession = await RegisterCurrentUiAsync(pipeName);
            var exit = await SendAsync<OperationResult>(pipeName, "SafeApplicationExit",
                new SafeApplicationExitRequest(ownSession.Session.SessionId, [], DateTimeOffset.UtcNow)
                {
                    SessionCapability = ownSession.SessionCapability
                });
            Assert.True(exit.Success);
            await agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!otherProcess.HasExited)
                otherProcess.Kill(entireProcessTree: true);
            if (!agent.HasExited)
                agent.Kill(entireProcessTree: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Missing_UI_capability_refuses_firewall_mutation_before_any_firewall_operation()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-firewall-auth-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid().ToString("N");
        var pipeName = ChunkPilotConstants.PipeNameFor(instanceId);
        var definition = await CreateStoredFakeServerAsync(root, "normal", TestPortAllocator.Reserve());
        using var agent = StartAgent(root, instanceId);
        try
        {
            await WaitForAgentAsync(pipeName);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SendAsync<FirewallElevationTicket>(pipeName, "PrepareFirewallAccess",
                    new PrepareFirewallAccessRequest(
                        definition.Id, FirewallHelperOperation.Create, PublicApproved: false)
                    {
                        ConnectivityOperation = PublicConnectivityOperation.PrepareFirewallAccess
                    }));
            Assert.Contains("capability", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(agent.HasExited);
            Assert.True((await SendAsync<OperationResult>(pipeName, "ShutdownAgent")).Success);
            await agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!agent.HasExited)
                agent.Kill(entireProcessTree: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Missing_UI_capability_refuses_server_port_change_before_properties_or_definition_mutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-properties-auth-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid().ToString("N");
        var pipeName = ChunkPilotConstants.PipeNameFor(instanceId);
        var originalPort = TestPortAllocator.Reserve();
        var replacementPort = TestPortAllocator.Reserve();
        var definition = await CreateStoredFakeServerAsync(root, "normal", originalPort);
        var propertiesPath = Path.Combine(definition.RootPath, "server.properties");
        await File.WriteAllTextAsync(propertiesPath, $"server-port={originalPort}\r\n");
        using var agent = StartAgent(root, instanceId);
        try
        {
            await WaitForAgentAsync(pipeName);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SendAsync<OperationResult>(pipeName, "UpdateServerProperties",
                    new ServerPropertiesRequest(definition.Id, new Dictionary<string, string>
                    {
                        ["server-port"] = replacementPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    })
                    {
                        ConnectivityOperation = PublicConnectivityOperation.UpdateServerProperties
                    }));
            Assert.Contains("capability", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal($"server-port={originalPort}\r\n", await File.ReadAllTextAsync(propertiesPath));
            Assert.Equal(originalPort,
                (await SendAsync<DashboardSnapshot>(pipeName, "Dashboard")).Servers
                .Single(server => server.Definition.Id == definition.Id).Definition.Port);

            var session = await RegisterCurrentUiAsync(pipeName);
            var exit = await SendAsync<OperationResult>(pipeName, "SafeApplicationExit",
                new SafeApplicationExitRequest(session.Session.SessionId, [], DateTimeOffset.UtcNow)
                {
                    SessionCapability = session.SessionCapability
                });
            Assert.True(exit.Success);
            await agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!agent.HasExited)
                agent.Kill(entireProcessTree: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Replacement_agent_exit_terminates_a_verified_detached_server_and_releases_its_port()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-detached-agent-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid().ToString("N");
        var pipeName = ChunkPilotConstants.PipeNameFor(instanceId);
        var port = TestPortAllocator.Reserve();
        var definition = await CreateStoredFakeServerAsync(root, "survive-eof", port);
        using var firstAgent = StartAgent(root, instanceId);
        Process? replacementAgent = null;
        int? detachedProcessId = null;
        try
        {
            await WaitForAgentAsync(pipeName);
            var firstSession = await RegisterCurrentUiAsync(pipeName);
            Assert.True((await SendAsync<OperationResult>(
                pipeName, "Start",
                AuthorizedServerRequest(definition.Id, firstSession, PublicConnectivityOperation.StartServer))).Success);
            Assert.True(IsPortListening(port), $"Fixture did not listen on port {port}.");

            await using (var store = new ChunkPilotStore(new AppDataPaths(root)))
            {
                await store.InitializeAsync();
                detachedProcessId = (await store.GetProcessIdentityAsync(definition.Id))?.ProcessId;
            }
            Assert.NotNull(detachedProcessId);

            firstAgent.Kill(entireProcessTree: false);
            await firstAgent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(IsPortListening(port), "The fixture must survive its first agent to exercise recovery.");

            replacementAgent = StartAgent(root, instanceId);
            await WaitForAgentAsync(pipeName);
            var session = await RegisterCurrentUiAsync(pipeName);
            var recovered = (await SendAsync<DashboardSnapshot>(pipeName, "Dashboard")).Servers
                .Single(server => server.Definition.Id == definition.Id);
            Assert.Equal(ServerState.Unknown, recovered.State);

            var stop = await SendAsync<OperationResult>(pipeName, "Stop",
                AuthorizedStopRequest(definition.Id, session));
            Assert.False(stop.Success);
            Assert.True(stop.RequiresForceConfirmation);
            var exit = await SendAsync<OperationResult>(pipeName, "SafeApplicationExit",
                new SafeApplicationExitRequest(session.Session.SessionId, [definition.Id], DateTimeOffset.UtcNow)
                {
                    SessionCapability = session.SessionCapability
                });
            Assert.True(exit.Success, exit.Message);
            await replacementAgent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(IsPortListening(port), $"Port {port} remained occupied after detached recovery.");
            Assert.False(IsProcessAlive(detachedProcessId.Value),
                $"Detached process {detachedProcessId} survived replacement-agent exit.");
        }
        finally
        {
            if (!firstAgent.HasExited)
                firstAgent.Kill(entireProcessTree: true);
            if (replacementAgent is { HasExited: false })
                replacementAgent.Kill(entireProcessTree: true);
            replacementAgent?.Dispose();
            if (detachedProcessId is { } processId)
            {
                try
                {
                    using var detached = Process.GetProcessById(processId);
                    if (!detached.HasExited)
                        detached.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
            }
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Minimize_keeps_hosting_then_WM_CLOSE_is_not_cancelled_and_leaves_no_invisible_UI_process()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-wm-close-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid().ToString("N");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = AppExe(),
                WorkingDirectory = Path.GetDirectoryName(AppExe())!,
                UseShellExecute = false
            }
        };
        process.StartInfo.Environment["CHUNKPILOT_DATA_ROOT"] = root;
        process.StartInfo.Environment["CHUNKPILOT_INSTANCE_ID"] = instanceId;
        Assert.True(process.Start());
        try
        {
            Assert.True(process.WaitForInputIdle(15_000));
            var windowDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < windowDeadline && !process.HasExited)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    break;
                await Task.Delay(100);
            }
            Assert.NotEqual(IntPtr.Zero, process.MainWindowHandle);
            Assert.True(ShowWindow(process.MainWindowHandle, ShowWindowMinimize));
            await Task.Delay(750);
            Assert.False(process.HasExited);
            Assert.True(await TryPingAsync(ChunkPilotConstants.PipeNameFor(instanceId)));
            _ = ShowWindow(process.MainWindowHandle, ShowWindowRestore);
            await Task.Delay(100);
            var stopwatch = Stopwatch.StartNew();
            Assert.True(process.CloseMainWindow());
            await Task.Delay(50);
            _ = process.CloseMainWindow();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                $"WM_CLOSE took {stopwatch.Elapsed}.");
            Assert.True(process.HasExited);
            await Task.Delay(500);
            var agentStillRunning = await TryPingAsync(ChunkPilotConstants.PipeNameFor(instanceId));
            if (agentStillRunning)
                _ = await SendAsync<OperationResult>(
                    ChunkPilotConstants.PipeNameFor(instanceId), "ShutdownAgent");
            Assert.False(agentStillRunning);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<T> SendAsync<T>(string pipeName, string operation, object? payload = null)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(1_000);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65_536, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65_536, leaveOpen: true) { AutoFlush = true };
        var request = new AgentRequest
        {
            Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload ?? new { }, ProtocolJson.Options)
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
        var response = JsonSerializer.Deserialize<AgentResponse>(await reader.ReadLineAsync() ?? "", ProtocolJson.Options)
                       ?? throw new IOException("Invalid response.");
        if (!response.Success)
            throw new InvalidOperationException(response.Error);
        return response.Payload!.Value.Deserialize<T>(ProtocolJson.Options)
               ?? throw new IOException("Invalid payload.");
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static string DotnetPath() => IntegrationTestRuntime.DotnetPath(RepositoryRoot());
    private static string AgentDll() => Path.Combine(RepositoryRoot(), "src", "ChunkPilot.Agent", "bin", "Release", "net10.0", "ChunkPilot.Agent.dll");
    private static string AppExe() => Path.Combine(RepositoryRoot(), "src", "ChunkPilot.App", "bin", "Release",
        "net10.0-windows", "ChunkPilot.exe");

    private static Process StartAgent(string root, string instanceId)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = DotnetPath(),
                Arguments = CommandLineQuoter.QuoteWindowsArgument(AgentDll()),
                WorkingDirectory = RepositoryRoot(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.Environment["CHUNKPILOT_DATA_ROOT"] = root;
        process.StartInfo.Environment["CHUNKPILOT_INSTANCE_ID"] = instanceId;
        Assert.True(process.Start());
        return process;
    }

    private static Process StartSyntheticUiOwner(string pipeName)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = DotnetPath(),
                Arguments = $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} ui-session-owner " +
                            CommandLineQuoter.QuoteWindowsArgument(pipeName),
                WorkingDirectory = RepositoryRoot(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        Assert.True(process.Start());
        return process;
    }

    private static Process StartPassiveProcess()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = DotnetPath(),
                Arguments = $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} survive-eof",
                WorkingDirectory = RepositoryRoot(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        Assert.True(process.Start());
        return process;
    }

    private static async Task<UiSessionRegistrationResult> ReadSyntheticUiSessionAsync(Process owner)
    {
        var line = await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var response = JsonSerializer.Deserialize<AgentResponse>(line ?? "", ProtocolJson.Options)
                       ?? throw new IOException("Synthetic UI returned no registration response.");
        if (!response.Success)
            throw new InvalidOperationException(response.Error);
        return response.Payload!.Value.Deserialize<UiSessionRegistrationResult>(ProtocolJson.Options)
               ?? throw new IOException("Synthetic UI returned an invalid registration payload.");
    }

    private static Task<UiSessionRegistrationResult> RegisterCurrentUiAsync(string pipeName)
    {
        using var current = Process.GetCurrentProcess();
        return RegisterUiAsync(pipeName, current);
    }

    private static Task<UiSessionRegistrationResult> RegisterUiAsync(string pipeName, Process owner)
    {
        var creation = ProcessCreationIdentity.Of(owner.SafeHandle);
        Assert.NotEqual(ProcessCreationIdentity.Unknown, creation);
        return SendAsync<UiSessionRegistrationResult>(pipeName, "RegisterUiSession",
            new UiSessionRegistrationRequest(owner.Id, creation));
    }

    private static ServerIdRequest AuthorizedServerRequest(
        Guid serverId,
        UiSessionRegistrationResult session,
        PublicConnectivityOperation operation) =>
        new(serverId)
        {
            Session = new UiSessionCredential
            {
                SessionId = session.Session.SessionId,
                Capability = session.SessionCapability
            },
            ConnectivityOperation = operation
        };

    private static StopRequest AuthorizedStopRequest(Guid serverId, UiSessionRegistrationResult session) =>
        new(serverId)
        {
            Session = new UiSessionCredential
            {
                SessionId = session.Session.SessionId,
                Capability = session.SessionCapability
            },
            ConnectivityOperation = PublicConnectivityOperation.StopServer
        };

    private static async Task WaitForAgentAsync(string pipeName)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if ((await SendAsync<OperationResult>(pipeName, "Ping")).Success)
                    return;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or JsonException)
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException("Agent did not start.");
    }

    private static async Task<bool> TryPingAsync(string pipeName)
    {
        try
        {
            return (await SendAsync<OperationResult>(pipeName, "Ping")).Success;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the fake server this fixture drives, on a port reserved for this test alone.
    /// </summary>
    /// <remarks>
    /// The port is required rather than defaulted. It used to default to 25565, so any machine running
    /// a real Minecraft server failed these tests for a reason that had nothing to do with the agent:
    /// CP-2026-014. A fixture never assumes a well-known port is free.
    /// </remarks>
    private static async Task<ServerDefinition> CreateStoredFakeServerAsync(
        string dataRoot,
        string mode,
        int port)
    {
        var serverRoot = Path.Combine(dataRoot, "server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(serverRoot);
        var definition = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Safe exit fixture",
            RootPath = serverRoot,
            Executable = DotnetPath(),
            Arguments = $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} {mode}",
            WorkingDirectory = serverRoot,
            ReadinessPattern = @"Done \(.+?\)!|For help, type",
            StartupTimeoutSeconds = 10,
            ShutdownTimeoutSeconds = 8,
            SaveTimeoutSeconds = 3,
            Port = port,
            Environment = new Dictionary<string, string>
            {
                ["CHUNKPILOT_FAKE_STATUS_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        await using var store = new ChunkPilotStore(new AppDataPaths(dataRoot));
        await store.InitializeAsync();
        await store.UpsertServerAsync(definition);
        return definition;
    }

    private static string FakeServerDll() =>
        Path.Combine(RepositoryRoot(), "tests", "ChunkPilot.FakeServer", "bin", "Release", "net10.0",
            "ChunkPilot.FakeServer.dll");

    private static bool IsPortListening(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private const int ShowWindowMinimize = 6;
    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}

[CollectionDefinition("Agent pipe", DisableParallelization = true)]
public sealed class AgentPipeCollection;
