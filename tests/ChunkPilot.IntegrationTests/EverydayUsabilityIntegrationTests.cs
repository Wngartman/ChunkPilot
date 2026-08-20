using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.IntegrationTests;

public sealed class EverydayUsabilityIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-usability-" + Guid.NewGuid().ToString("N"));
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

    [Fact(Timeout = 40_000)]
    public async Task Running_server_rename_is_metadata_only_and_persists()
    {
        var serverRoot = Path.Combine(root, "Server [kept] & files");
        Directory.CreateDirectory(serverRoot);
        var original = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            RootPath = serverRoot,
            WorkingDirectory = serverRoot,
            Executable = DotnetPath(),
            Arguments = $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} normal",
            ReadinessPattern = @"Done \(.+?\)!|For help, type",
            StartupTimeoutSeconds = 10,
            ShutdownTimeoutSeconds = 5,
            SaveTimeoutSeconds = 5,
            Port = GetFreePort(),
            IsManaged = false
        };
        var supervisor = CreateSupervisor();
        await supervisor.InitializeAsync();
        await supervisor.ImportAsync(original);
        var managed = supervisor.Get(original.Id);
        try
        {
            Assert.True((await managed.StartAsync()).Success);
            var processBefore = await store.GetProcessIdentityAsync(original.Id);

            var result = await supervisor.RenameAsync(original.Id, "  世界 Survival  ");

            Assert.True(result.Success, result.Message);
            Assert.Equal(ServerState.Running, managed.State);
            Assert.Equal("世界 Survival", managed.Definition.Name);
            Assert.Equal(original with { Name = "世界 Survival" }, managed.Definition);
            Assert.Equal(processBefore, await store.GetProcessIdentityAsync(original.Id));
            var persisted = Assert.Single(await store.GetServersAsync());
            Assert.Equal(managed.Definition.Id, persisted.Id);
            Assert.Equal(managed.Definition.Name, persisted.Name);
            Assert.Equal(managed.Definition.RootPath, persisted.RootPath);
            Assert.Equal(managed.Definition.Executable, persisted.Executable);
            Assert.Equal(managed.Definition.Arguments, persisted.Arguments);
            Assert.Contains(await store.GetActivityAsync(), entry =>
                entry.ServerId == original.Id && entry.Action == "Rename display name");
        }
        finally
        {
            if (managed.State is not ServerState.Stopped)
                _ = await managed.ForceTerminateAsync("Test cleanup");
            await supervisor.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("bad:name")]
    public async Task Invalid_rename_preserves_the_prior_definition(string requested)
    {
        var serverRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(serverRoot);
        var original = new ServerDefinition { Id = Guid.NewGuid(), Name = "Kept", RootPath = serverRoot };
        await using var supervisor = CreateSupervisor();
        await supervisor.InitializeAsync();
        await supervisor.ImportAsync(original);

        var result = await supervisor.RenameAsync(original.Id, requested);

        Assert.False(result.Success);
        Assert.Equal(original, supervisor.Get(original.Id).Definition);
        var persisted = Assert.Single(await store.GetServersAsync());
        Assert.Equal(original.Id, persisted.Id);
        Assert.Equal(original.Name, persisted.Name);
        Assert.Equal(original.RootPath, persisted.RootPath);
    }

    private ServerSupervisor CreateSupervisor() => new(store, paths, new ProcessStatisticsProvider(),
        new MinecraftStatusClient(), new BackupService(paths, store), loggerFactory);

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate ChunkPilot repository root.");
    }

    private static string DotnetPath() => Path.Combine(RepositoryRoot(), ".tools", "dotnet", "dotnet.exe");
    private static string FakeServerDll() => Path.Combine(RepositoryRoot(), "tests", "ChunkPilot.FakeServer", "bin",
        "Release", "net10.0", "ChunkPilot.FakeServer.dll");

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
