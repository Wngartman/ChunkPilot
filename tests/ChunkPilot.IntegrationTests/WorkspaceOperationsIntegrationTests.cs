using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// The Agent-side behaviour the workspace depends on, against a real child process.
/// </summary>
/// <remarks>
/// These use the fake server rather than a mock: the point of every one of them is what happens across
/// the process boundary - a reply arriving on stdout, a save being frozen before files are read, a
/// player appearing and leaving. None of it can be exercised in-process.
/// </remarks>
public sealed class WorkspaceOperationsIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-workspace-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>A moderation action is only reported as done once the server has said so.</summary>
    [Fact(Timeout = 40_000)]
    public async Task Moderation_waits_for_the_servers_own_confirmation()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);

        var granted = await server.ModeratePlayerAsync(PlayerModerationAction.GrantOperator, "Xustar");
        Assert.True(granted.Success, granted.Message);
        Assert.Contains("Made Xustar a server operator", granted.Message, StringComparison.Ordinal);

        var removed = await server.ModeratePlayerAsync(PlayerModerationAction.RemoveOperator, "Xustar");
        Assert.True(removed.Success, removed.Message);
        Assert.Contains("no longer a server operator", removed.Message, StringComparison.Ordinal);

        // The fixture's unknown player produces the refusal the real server produces.
        var refused = await server.ModeratePlayerAsync(PlayerModerationAction.AddToWhitelist, "Ghost");
        Assert.False(refused.Success);
        Assert.Contains("does not exist", refused.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True((await server.StopAsync()).Success);
    }

    /// <summary>Moderation needs a running server, and says so rather than pretending.</summary>
    [Fact(Timeout = 30_000)]
    public async Task Moderation_on_a_stopped_server_is_refused_with_its_state()
    {
        await using var server = CreateManaged("normal");

        var result = await server.ModeratePlayerAsync(PlayerModerationAction.GrantOperator, "Xustar");

        Assert.False(result.Success);
        Assert.Contains("running server", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An invalid player name never reaches the console.</summary>
    [Fact(Timeout = 30_000)]
    public async Task An_invalid_player_name_is_rejected_before_any_command_is_sent()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);

        await Assert.ThrowsAsync<ArgumentException>(
            () => server.ModeratePlayerAsync(PlayerModerationAction.Ban, "not a player name"));

        var lines = server.Snapshot(500).Console.Select(line => line.Text).ToArray();
        Assert.DoesNotContain(lines, line => line.Contains("ban not", StringComparison.OrdinalIgnoreCase));
        Assert.True((await server.StopAsync()).Success);
    }

    /// <summary>Presence comes from the server's own join and leave lines, and moves the stamp.</summary>
    [Fact(Timeout = 40_000)]
    public async Task Players_appear_and_leave_as_the_server_reports_them()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);
        var initialStamp = server.Snapshot(0).PlayerAccessStamp;
        Assert.Empty(server.OnlinePlayerNames);

        Assert.True((await server.SendCommandAsync("fixture-join Xustar")).Success);
        await WaitUntil(() => server.OnlinePlayerNames.Contains("Xustar"));
        Assert.Single(server.OnlinePlayerNames);

        // The stamp has to move, because that is what tells the UI to re-read.
        await Task.Delay(1_100);
        Assert.NotEqual(initialStamp, server.Snapshot(0).PlayerAccessStamp);

        Assert.True((await server.SendCommandAsync("fixture-leave Xustar")).Success);
        await WaitUntil(() => server.OnlinePlayerNames.Count == 0);

        Assert.True((await server.StopAsync()).Success);
        Assert.Empty(server.OnlinePlayerNames);
    }

    /// <summary>A chat line quoting the join wording cannot invent a player.</summary>
    [Fact(Timeout = 40_000)]
    public async Task Chat_that_mentions_joining_does_not_add_a_player()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);

        Assert.True((await server.SendCommandAsync("say <Someone> nobody joined the game today")).Success);
        await Task.Delay(300);

        Assert.Empty(server.OnlinePlayerNames);
        Assert.True((await server.StopAsync()).Success);
    }

    /// <summary>Game rules are read from the running server, one round trip for all of them.</summary>
    [Fact(Timeout = 40_000)]
    public async Task Game_rules_are_read_from_the_running_server()
    {
        await using var server = CreateManaged("normal");
        Assert.True((await server.StartAsync()).Success);

        var reported = await server.QueryGamerulesAsync(["keepInventory", "randomTickSpeed"]);

        Assert.Equal("false", reported.Reported["keepInventory"]);
        Assert.Equal("3", reported.Reported["randomTickSpeed"]);
        Assert.Empty(reported.Rejected);

        Assert.True((await server.SendCommandAsync("gamerule keepInventory true")).Success);
        await Task.Delay(200);
        var afterChange = await server.QueryGamerulesAsync(["keepInventory"]);
        Assert.Equal("true", afterChange.Reported["keepInventory"]);

        // The exact-rule path also works for a name not yet in ChunkPilot's curated table. The
        // running server, not a hard-coded version list, remains the authority.
        Assert.True((await server.SendCommandAsync("gamerule futureRule_26 enabled")).Success);
        await Task.Delay(200);
        var versionSpecific = await server.QueryGamerulesAsync(["futureRule_26"]);
        Assert.Equal("enabled", versionSpecific.Reported["futureRule_26"]);

        Assert.True((await server.StopAsync()).Success);
    }

    /// <summary>A stopped server reports no values rather than guessing at them.</summary>
    [Fact(Timeout = 30_000)]
    public async Task A_stopped_server_reports_no_game_rule_values()
    {
        await using var server = CreateManaged("normal");

        var result = await server.QueryGamerulesAsync(["keepInventory"]);

        Assert.Empty(result.Reported);
        Assert.Empty(result.Rejected);
    }

    /// <summary>
    /// A rule the server refuses is recorded as rejected, not left as an unknown value.
    /// </summary>
    /// <remarks>
    /// This is what Minecraft 26.2 does to every rule name ChunkPilot knows: Brigadier answers
    /// "Incorrect argument for command" and echoes the command with a caret at the point of failure.
    /// Recording that is what stops the interface offering a switch that cannot work.
    /// </remarks>
    [Fact(Timeout = 40_000)]
    public async Task A_rule_the_server_refuses_is_recorded_as_rejected()
    {
        await using var server = CreateManaged("reject-gamerules");
        Assert.True((await server.StartAsync()).Success);

        var result = await server.QueryGamerulesAsync(["keepInventory", "spawnRadius"]);

        Assert.Empty(result.Reported);
        Assert.Equal(2, result.Rejected.Count);
        Assert.Contains("keepInventory", result.Rejected);
        Assert.Contains("spawnRadius", result.Rejected);
        Assert.True((await server.StopAsync()).Success);
    }

    /// <summary>
    /// A running backup freezes saving, flushes, copies, and turns saving back on.
    /// </summary>
    /// <remarks>
    /// The order is the whole point: files copied while the server is still writing them are a backup of
    /// a moment that never existed. save-on is in a finally, so it is restored even when the copy fails.
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task A_running_backup_freezes_saving_flushes_and_restores_it()
    {
        await using var server = CreateManaged("normal");
        await SeedWorldAsync(server.Definition.RootPath);
        var supervisor = await SupervisorFor(server);
        Assert.True((await server.StartAsync()).Success);

        var record = await supervisor.BackupAsync(server.Definition.Id, "Manual");

        Assert.True(record.Verified, record.VerificationMessage);
        Assert.True(File.Exists(record.ArchivePath));

        var lines = server.Snapshot(1_000).Console.Select(line => line.Text).ToArray();
        var saveOff = Array.FindIndex(lines, line => line.Contains("> save-off", StringComparison.Ordinal));
        var flush = Array.FindIndex(lines, line => line.Contains("> save-all flush", StringComparison.Ordinal));
        var saveOn = Array.FindIndex(lines, line => line.Contains("> save-on", StringComparison.Ordinal));
        Assert.True(saveOff >= 0 && flush > saveOff && saveOn > flush,
            $"Observed save-off={saveOff}, flush={flush}, save-on={saveOn}");

        Assert.Equal(ServerState.Running, server.State);
        Assert.True((await server.StopAsync()).Success);
    }

    /// <summary>A stopped server backs up too, without sending anything to a console that is not there.</summary>
    [Fact(Timeout = 60_000)]
    public async Task A_stopped_backup_succeeds_and_sends_no_commands()
    {
        await using var server = CreateManaged("normal");
        await SeedWorldAsync(server.Definition.RootPath);
        var supervisor = await SupervisorFor(server);

        var record = await supervisor.BackupAsync(server.Definition.Id, "Manual");

        Assert.True(record.Verified, record.VerificationMessage);
        Assert.DoesNotContain(server.Snapshot(500).Console,
            line => line.Text.Contains("> save-off", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private static async Task SeedWorldAsync(string serverRoot)
    {
        Directory.CreateDirectory(Path.Combine(serverRoot, "world", "region"));
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "server.properties"), "motd=fixture\r\n");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "world", "level.dat"), "world-state");
        await File.WriteAllTextAsync(
            Path.Combine(serverRoot, "world", "region", "r.0.0.mca"), "region-state");
    }

    private async Task<ServerSupervisor> SupervisorFor(ManagedServer server)
    {
        var supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), new BackupService(paths, store), loggerFactory);
        await supervisor.InitializeAsync();
        // Import registers the definition; the supervisor's own instance is the one it backs up, so the
        // console assertions read the process this test started.
        await supervisor.ImportAsync(server.Definition);
        typeof(ServerSupervisor)
            .GetField("servers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(supervisor)
            .As<System.Collections.Concurrent.ConcurrentDictionary<Guid, ManagedServer>>()[server.Definition.Id] = server;
        return supervisor;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        Assert.True(condition(), "The expected state never arrived.");
    }

    private ManagedServer CreateManaged(string mode)
    {
        var serverRoot = Path.Combine(root, "servers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(serverRoot);
        var definition = new ServerDefinition
        {
            Name = $"Workspace {mode}",
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
        return new ManagedServer(definition, new ProcessStatisticsProvider(), new MinecraftStatusClient(),
            store, paths, loggerFactory.CreateLogger<ManagedServer>(), consoleCapacity: 2_000);
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
        Path.Combine(RepositoryRoot(), "tests", "ChunkPilot.FakeServer", "bin", "Release", "net10.0",
            "ChunkPilot.FakeServer.dll");

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        loggerFactory.Dispose();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}

internal static class ReflectionCastExtensions
{
    /// <summary>Casts a reflected field value, so a test can inject the instance it already owns.</summary>
    public static T As<T>(this object? value) where T : class =>
        value as T ?? throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
