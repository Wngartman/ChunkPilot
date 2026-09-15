using System.Net;
using System.Net.Sockets;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.IntegrationTests;

public sealed class StoppedDataOperationSafetyTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-stopped-data-" + Guid.NewGuid().ToString("N"));
    private AppDataPaths paths = null!;
    private ChunkPilotStore store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        paths = new AppDataPaths(Path.Combine(root, "data"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
    }

    [Theory]
    [InlineData("persisted", true)]
    [InlineData("persisted", false)]
    [InlineData("tcp", true)]
    [InlineData("tcp", false)]
    [InlineData("udp", true)]
    [InlineData("udp", false)]
    public async Task Stopped_mutation_and_nonrunning_backup_require_absent_process_identity_and_listeners(
        string evidence, bool requireStopped)
    {
        using var tcp = evidence == "tcp" ? new TcpListener(IPAddress.Loopback, 0) : null;
        tcp?.Start();
        using var udp = evidence == "udp" ? new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) : null;
        var port = tcp is not null ? ((IPEndPoint)tcp.LocalEndpoint).Port :
            udp is not null ? ((IPEndPoint)udp.Client.LocalEndPoint!).Port : TestPortAllocator.Reserve();
        var folder = Path.Combine(root, "server");
        Directory.CreateDirectory(folder);
        var sentinel = Path.Combine(folder, "world-state.txt");
        await File.WriteAllTextAsync(sentinel, "preserve-current-world");
        var definition = new ServerDefinition { Name = "Synthetic stopped proof", RootPath = folder, Port = port };
        await store.UpsertServerAsync(definition);
        if (evidence == "persisted")
            await store.UpsertProcessIdentityAsync(new ProcessIdentity { ServerId = definition.Id, ProcessId = int.MaxValue });
        await using var managed = new ManagedServer(definition, new ProcessStatisticsProvider(), new MinecraftStatusClient(),
            store, paths, NullLogger<ManagedServer>.Instance);
        Assert.Equal(ServerState.Stopped, managed.State);
        Assert.False(managed.HasExactOwnedProcessAlive());
        var entered = false;

        Task<bool> AttemptAsync() => managed.RunExclusiveDataOperationAsync("synthetic data operation", requireStopped,
            saveIfRunning: !requireStopped, freezeWorldSaving: false, async token =>
            {
                entered = true;
                await File.WriteAllTextAsync(sentinel, "operation-committed", token);
                return true;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(AttemptAsync);
        Assert.False(entered);
        Assert.Equal("preserve-current-world", await File.ReadAllTextAsync(sentinel));
        if (evidence == "persisted")
        {
            Assert.NotNull(await store.GetProcessIdentityAsync(definition.Id));
            await store.RemoveProcessIdentityAsync(definition.Id);
        }
        tcp?.Stop();
        udp?.Dispose();
        // Clearing the synthetic evidence permits the same queued operation, proving refusal
        // released the gate and did not alter the unrelated file or process-identity record.
        Assert.True(await AttemptAsync());
        Assert.Equal("operation-committed", await File.ReadAllTextAsync(sentinel));
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }
}
