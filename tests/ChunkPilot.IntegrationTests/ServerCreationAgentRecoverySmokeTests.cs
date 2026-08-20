using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// Runtime proof that the shipped Agent reconciles an interrupted creation when it next starts.
/// </summary>
/// <remarks>
/// The unit and transaction tests exercise the recovery service directly. This one launches the real
/// Agent binary against an isolated data root, which is the only way to show that the reconciliation
/// is actually wired into startup and runs before the server list is read. No provider is contacted,
/// no Java is used and no Minecraft process is started: the "server" is a directory with a text file
/// in it.
/// </remarks>
[Collection("Agent pipe")]
public sealed class ServerCreationAgentRecoverySmokeTests
{
    [Fact(Timeout = 90_000)]
    public async Task The_agent_finishes_an_interrupted_creation_on_its_next_start()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-creation-smoke-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid().ToString("N");
        var pipeName = ChunkPilotConstants.PipeNameFor(instanceId);
        Directory.CreateDirectory(root);

        var paths = new AppDataPaths(root);
        var operationId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var instanceRoot = Path.Combine(root, "Servers");
        var destination = Path.Combine(instanceRoot, "Interrupted-Server");
        var staging = Path.Combine(instanceRoot, ServerCreationTransaction.StagingFolderName(operationId));

        // The durable state a crash between promotion and persistence leaves behind: the folder is in
        // place and carries this operation's marker, and no server record exists.
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "server.jar"), "smoke fixture jar");
        await File.WriteAllTextAsync(Path.Combine(destination, "eula.txt"), "eula=true");
        await CreationOwnershipMarker.WriteAsync(destination, new CreationOwnershipMarker(
            CreationOwnershipMarker.CurrentSchemaVersion, operationId, serverId,
            CreationPathSafety.Canonical(destination), DateTimeOffset.UtcNow), CancellationToken.None);

        var definition = new ServerDefinition
        {
            Id = serverId,
            Name = "Interrupted Server",
            RootPath = destination,
            WorkingDirectory = destination,
            Executable = "java",
            Arguments = "-jar server.jar nogui",
            Ecosystem = ServerEcosystem.Vanilla,
            MinecraftVersion = "1.21.1",
            IsManaged = true,
            ManagedInstanceRoot = instanceRoot
        };

        await using (var store = new ChunkPilotStore(paths))
        {
            await store.InitializeAsync();
            await store.UpsertCreationJournalAsync(new CreationJournalEntry
            {
                OperationId = operationId,
                ServerId = serverId,
                CreationKind = "LocalZip",
                ServerName = definition.Name,
                CanonicalDestination = CreationPathSafety.Canonical(destination),
                CanonicalStaging = CreationPathSafety.Canonical(staging),
                InstanceRoot = CreationPathSafety.Canonical(instanceRoot),
                StartedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Phase = CreationPhase.Activated,
                LastCompletedCheckpoint = CreationPhase.Activated,
                ActivationBegan = true,
                ActivationCompleted = true,
                ActivationMode = CreationActivationMode.DirectoryMove,
                OwnershipMarkerFile = CreationOwnershipMarker.FileName,
                PlannedDefinition = definition
            });
            Assert.Empty(await store.GetServersAsync());
        }
        SqliteConnection.ClearAllPools();

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
            // The Agent answering at all means startup finished, and reconciliation runs before the
            // supervisor reads the server list.
            OperationResult? ready = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline && ready is null)
            {
                try { ready = await SendAsync<OperationResult>(pipeName, "Ping"); }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    await Task.Delay(200);
                }
            }
            Assert.NotNull(ready);

            var dashboard = await SendAsync<DashboardSnapshot>(pipeName, "Dashboard");
            var recovered = Assert.Single(dashboard.Servers, snapshot => snapshot.Definition.Id == serverId);
            Assert.Equal(CreationPathSafety.Canonical(destination),
                CreationPathSafety.Canonical(recovered.Definition.RootPath));
            Assert.True(recovered.Definition.IsManaged);

            await using (var verify = new ChunkPilotStore(paths))
            {
                var registered = Assert.Single(await verify.GetServersAsync());
                Assert.Equal(serverId, registered.Id);
                // Finalised: the journal is closed and the temporary marker is gone.
                Assert.Empty(await verify.GetCreationJournalsAsync());
            }
            Assert.False(File.Exists(CreationOwnershipMarker.PathIn(destination)));
            Assert.True(File.Exists(Path.Combine(destination, "server.jar")));

            var shutdown = await SendAsync<OperationResult>(pipeName, "ShutdownAgent");
            Assert.True(shutdown.Success);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<T> SendAsync<T>(string pipeName, string operation, object? payload = null)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(1_000);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65_536, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65_536, leaveOpen: true)
        {
            AutoFlush = true
        };
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

    private static string DotnetPath() => Path.Combine(RepositoryRoot(), ".tools", "dotnet", "dotnet.exe");

    private static string AgentDll() => Path.Combine(RepositoryRoot(), "src", "ChunkPilot.Agent",
        "bin", "Release", "net10.0", "ChunkPilot.Agent.dll");
}
