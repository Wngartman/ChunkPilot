using System.Net;
using System.Net.Sockets;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.IntegrationTests;

public sealed class ServerBindingIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-binding-agent-" + Guid.NewGuid().ToString("N"));
    private AppDataPaths paths = null!;
    private ChunkPilotStore store = null!;
    private ILoggerFactory logging = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        paths = new AppDataPaths(Path.Combine(root, "data"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        logging = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    }

    [Fact(Timeout = 45_000)]
    public async Task Explicit_apply_restart_and_stopped_apply_preserve_loopback_runtime_and_other_files()
    {
        var definition = Definition();
        var properties = Path.Combine(definition.RootPath, "server.properties");
        var original = $"# synthetic bind configuration\nserver-ip=127.0.0.1\nserver-port={definition.Port}\nlevel-name=untouched\nonline-mode=true\n";
        await File.WriteAllTextAsync(properties, original);
        await File.WriteAllTextAsync(Path.Combine(definition.RootPath, "user.txt"), "untouched");
        var files = new SafeFileService(paths);
        await using var server = new ManagedServer(definition, new ProcessStatisticsProvider(), new MinecraftStatusClient(),
            store, paths, logging.CreateLogger<ManagedServer>());
        await server.SaveNetworkConfigurationAsync(new() { ServerId = definition.Id, Mode = NetworkMode.ThisComputerOnly }, CancellationToken.None);
        var stopped = await server.ApplyServerBindingAsync(new(definition.Id, NetworkMode.ThisComputerOnly, true, false), files);
        Assert.True(stopped.Success, stopped.Message);
        Assert.Equal(ServerState.Stopped, server.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.ApplyServerBindingAsync(new(definition.Id, NetworkMode.ThisComputerOnly, false, false), files));
        var start = await server.StartAsync();
        Assert.True(start.Success, start.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.ApplyServerBindingAsync(new(definition.Id, NetworkMode.ThisComputerOnly, true, false), files));
        var result = await server.ApplyServerBindingAsync(new(definition.Id, NetworkMode.ThisComputerOnly, true, true), files);
        Assert.True(result.Success, result.Message);
        Assert.Equal(ServerState.Running, server.State);
        Assert.Equal(original, await File.ReadAllTextAsync(properties));
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(definition.RootPath, "user.txt")));
        Assert.True((await server.StopAsync()).Success);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Expired_native_session_before_or_after_admission_never_changes_binding(int rejectAt)
    {
        var definition = Definition();
        var properties = Path.Combine(definition.RootPath, "server.properties");
        var original = $"# synthetic protected configuration\nserver-ip=127.0.0.1\nserver-port={definition.Port}\n";
        await File.WriteAllTextAsync(properties, original);
        await using var server = new ManagedServer(definition, new ProcessStatisticsProvider(), new MinecraftStatusClient(),
            store, paths, logging.CreateLogger<ManagedServer>());
        await server.SaveNetworkConfigurationAsync(new() { ServerId = definition.Id, Mode = NetworkMode.HomeNetwork }, CancellationToken.None);
        var demands = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => server.ApplyServerBindingAsync(
            new(definition.Id, NetworkMode.HomeNetwork, true, false), new SafeFileService(paths), () =>
            {
                if (++demands == rejectAt) throw new UnauthorizedAccessException("The synthetic session expired.");
            }));
        Assert.Equal(rejectAt, demands);
        Assert.Equal(original, await File.ReadAllTextAsync(properties));
        Assert.Equal(ServerState.Stopped, server.State);
    }

    [Fact(Timeout = 45_000)]
    public async Task Empty_stop_rechecks_current_process_and_roster_inside_the_lifecycle_gate()
    {
        var definition = Definition();
        await using var server = new ManagedServer(definition, new ProcessStatisticsProvider(), new MinecraftStatusClient(),
            store, paths, logging.CreateLogger<ManagedServer>());
        Assert.True((await server.StartAsync()).Success);
        var original = server.Snapshot(0);
        Assert.False((await server.StopKnownEmptyAsync(original with { RootProcessCreationTicks = original.RootProcessCreationTicks + 1 })).Success);
        Assert.Equal(ServerState.Running, server.State);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!AutomationObservationPolicy.HasFreshCount(server.Snapshot(0), DateTimeOffset.UtcNow))
            await Task.Delay(100, deadline.Token);
        Assert.True((await server.StopKnownEmptyAsync(original)).Success);
        Assert.Equal(ServerState.Stopped, server.State);
    }

    private ServerDefinition Definition()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "ChunkPilot.sln"))) repo = repo.Parent;
        var repoRoot = repo?.FullName ?? throw new DirectoryNotFoundException();
        var serverRoot = Path.Combine(root, "server");
        Directory.CreateDirectory(serverRoot);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new()
        {
            Name = "Owned binding fixture", RootPath = serverRoot, WorkingDirectory = serverRoot,
            Executable = IntegrationTestRuntime.DotnetPath(repoRoot),
            Arguments = CommandLineQuoter.QuoteWindowsArgument(Path.Combine(repoRoot, "tests", "ChunkPilot.FakeServer", "bin", "Release", "net10.0", "ChunkPilot.FakeServer.dll")) + " normal",
            IsManaged = true, ManagedInstanceRoot = serverRoot, Ecosystem = ServerEcosystem.NeoForge, MinecraftVersion = "1.21.1",
            Port = port, StartupTimeoutSeconds = 10, ShutdownTimeoutSeconds = 5, SaveTimeoutSeconds = 5,
            Environment = new() { ["CHUNKPILOT_FAKE_STATUS_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
    }
    public Task DisposeAsync()
    {
        logging.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
        return Task.CompletedTask;
    }
}
